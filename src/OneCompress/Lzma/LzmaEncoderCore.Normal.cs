namespace RecursiveAI.OneCompress.Lzma;

public sealed partial class LzmaEncoderCore
{
    private const int Infinity = 1 << 30;
    private const int LengthSymbols = LzmaModel.MaxMatchLength - LzmaModel.MinMatchLength + 1;

    private const byte KindLiteral = 0;
    private const byte KindShortRep = 1;
    private const byte KindRep = 2; // 2 to 5: repeated distance 0 to 3
    private const byte KindMatch = 6;

    private struct Node
    {
        public int Price;
        public int Prev;
        public uint Dist;
        public int State;
        public uint R0;
        public uint R1;
        public uint R2;
        public uint R3;
        public byte Kind;
    }

    private Node[] _opt = [];
    private byte[] _opKind = [];
    private int[] _opLen = [];
    private uint[] _opDist = [];
    private int[] _slotPrice = [];
    private int[] _distPrice = [];
    private int[] _alignPrice = [];
    private int[] _lenPrice = [];
    private int _priceAge;
    private int _rebuildInterval;
    private int _end;

    private void InitializeNormalParser()
    {
        // Prices steer the choice of symbols; the normal parser needs them fresher than the fast one, which only compares a few candidates.
        _rebuildInterval = _settings.Mode == LzmaMode.Normal ? 256 : 2048;
        if (_settings.Mode == LzmaMode.Normal)
        {
            _opt = new Node[MaxParseLength + 2];
            _opKind = new byte[MaxParseLength + 2];
            _opLen = new int[MaxParseLength + 2];
            _opDist = new uint[MaxParseLength + 2];
        }

        _slotPrice = new int[LzmaModel.LengthToPositionStates * 64];
        _distPrice = new int[LzmaModel.LengthToPositionStates * 128];
        _alignPrice = new int[16];
        _lenPrice = new int[2 * LzmaModel.MaxPositionStates * LengthSymbols];
    }

    // The normal parser: shortest-path search over the next `horizon` bytes. Every position is a node; literals, short repeated matches,
    // repeated matches and the finder's matches are edges priced from the current adaptive probabilities. The cheapest path to the end
    // of the explored region is coded. A match of at least the nice length ends the search at once.
    private void ParseNormal(int horizon)
    {
        int p0 = _pos;
        int maxLen0 = Math.Min(Math.Min(_bufEnd - p0, LzmaModel.MaxMatchLength), horizon);
        int n = FindMatchesAt(p0, _matches);
        if (maxLen0 < 2)
        {
            EmitLiteralOrShortRep(p0);
            AdvanceFinder(_pos);
            return;
        }

        if (_priceAge >= _rebuildInterval)
        {
            UpdatePrices();
        }

        int nice = Math.Min(_settings.NiceLength, maxLen0);
        int repLen = 0;
        int repIndex = 0;
        for (int i = 0; i < 4; i++)
        {
            int l = RepLength(p0, _reps[i], maxLen0);
            if (l > repLen)
            {
                repLen = l;
                repIndex = i;
            }
        }

        if (repLen >= nice)
        {
            EmitRep(repIndex, repLen);
            _priceAge++;
            AdvanceFinder(_pos);
            return;
        }

        int mainLen = n > 0 ? Math.Min(_matches[n - 1].Length, maxLen0) : 0;
        if (mainLen >= nice)
        {
            EmitMatch(mainLen, (uint)_matches[n - 1].Distance - 1);
            _priceAge++;
            AdvanceFinder(_pos);
            return;
        }

        if (mainLen < 2 && repLen < 2)
        {
            EmitLiteralOrShortRep(p0);
            _priceAge++;
            AdvanceFinder(_pos);
            return;
        }

        ref Node first = ref _opt[0];
        first.Price = 0;
        first.State = _state;
        first.R0 = _reps[0];
        first.R1 = _reps[1];
        first.R2 = _reps[2];
        first.R3 = _reps[3];
        _end = 0;
        if (!Expand(0, p0, n, maxLen0, nice))
        {
            for (int cur = 1; cur < _end; cur++)
            {
                int p = p0 + cur;
                int maxLen = Math.Min(Math.Min(_bufEnd - p, LzmaModel.MaxMatchLength), horizon - cur);
                n = FindMatchesAt(p, _matches);
                if (Expand(cur, p, n, maxLen, Math.Min(_settings.NiceLength, maxLen)))
                {
                    break;
                }
            }
        }

        int count = 0;
        for (int idx = _end; idx > 0; idx = _opt[idx].Prev)
        {
            _opKind[count] = _opt[idx].Kind;
            _opLen[count] = idx - _opt[idx].Prev;
            _opDist[count] = _opt[idx].Dist;
            count++;
        }

        for (int k = count - 1; k >= 0; k--)
        {
            byte kind = _opKind[k];
            if (kind == KindLiteral)
            {
                EmitLiteral();
            }
            else if (kind == KindShortRep)
            {
                EmitShortRep();
            }
            else if (kind == KindMatch)
            {
                EmitMatch(_opLen[k], _opDist[k]);
            }
            else
            {
                EmitRep(kind - KindRep, _opLen[k]);
            }
        }

        _priceAge += count;
        AdvanceFinder(_pos);
    }

    // Relaxes the edges that leave node `cur` (position p). Returns true when a match of at least the nice length was found, in which
    // case the node it reaches is the end of the search.
    private bool Expand(int cur, int p, int matchCount, int maxLen, int nice)
    {
        ref Node node = ref _opt[cur];
        int price = node.Price;
        int state = node.State;
        uint r0 = node.R0;
        uint r1 = node.R1;
        uint r2 = node.R2;
        uint r3 = node.R3;
        int posState = (int)((_posBase + p) & _pbMask);
        int stateBase = (state << 4) + posState;
        ushort[] probs = _probs;
        int isMatch = probs[LzmaModel.IsMatch + stateBase];

        // Literal.
        int toLiteral = price + LzmaPrices.Zero(isMatch) + LiteralPrice(p, state, r0);
        Relax(cur + 1, toLiteral, cur, KindLiteral, 0, LzmaModel.NextStateAfterLiteral(state), r0, r1, r2, r3, false);

        // One byte at the last distance.
        int src0 = p - (int)r0 - 1;
        int isRep = probs[LzmaModel.IsRep + state];
        if (src0 >= 1 && _buf[src0] == _buf[p])
        {
            int toShort = price + LzmaPrices.One(isMatch) + LzmaPrices.One(isRep) + LzmaPrices.Zero(probs[LzmaModel.IsRepG0 + state]) + LzmaPrices.Zero(probs[LzmaModel.IsRep0Long + stateBase]);
            Relax(cur + 1, toShort, cur, KindShortRep, 0, LzmaModel.NextStateAfterShortRep(state), r0, r1, r2, r3, false);
        }

        if (maxLen < 2)
        {
            return false;
        }

        // Repeated matches.
        int repBase = price + LzmaPrices.One(isMatch) + LzmaPrices.One(isRep);
        int g0 = probs[LzmaModel.IsRepG0 + state];
        int g1 = probs[LzmaModel.IsRepG1 + state];
        int g2 = probs[LzmaModel.IsRepG2 + state];
        int newRepState = LzmaModel.NextStateAfterRep(state);
        for (int i = 0; i < 4; i++)
        {
            uint ri = i == 0 ? r0 : i == 1 ? r1 : i == 2 ? r2 : r3;
            int len = RepLength(p, ri, maxLen);
            if (len < 2)
            {
                continue;
            }

            int indexPrice = i switch
            {
                0 => LzmaPrices.Zero(g0) + LzmaPrices.One(probs[LzmaModel.IsRep0Long + stateBase]),
                1 => LzmaPrices.One(g0) + LzmaPrices.Zero(g1),
                2 => LzmaPrices.One(g0) + LzmaPrices.One(g1) + LzmaPrices.Zero(g2),
                _ => LzmaPrices.One(g0) + LzmaPrices.One(g1) + LzmaPrices.One(g2),
            };
            int toRep = repBase + indexPrice;
            uint n0 = i == 0 ? r0 : ri;
            uint n1 = i == 0 ? r1 : i == 1 ? r0 : r0;
            uint n2 = i <= 1 ? r2 : i == 2 ? r1 : r1;
            uint n3 = i <= 2 ? r3 : r2;
            int lenBase = (1 * LzmaModel.MaxPositionStates + posState) * LengthSymbols;
            if (len >= nice)
            {
                Relax(cur + len, toRep + _lenPrice[lenBase + len - 2], cur, (byte)(KindRep + i), 0, newRepState, n0, n1, n2, n3, true);
                return true;
            }

            for (int l = len; l >= 2; l--)
            {
                Relax(cur + l, toRep + _lenPrice[lenBase + l - 2], cur, (byte)(KindRep + i), 0, newRepState, n0, n1, n2, n3, false);
            }
        }

        // Matches from the finder.
        int matchBase = price + LzmaPrices.One(isMatch) + LzmaPrices.Zero(isRep);
        int newMatchState = LzmaModel.NextStateAfterMatch(state);
        int matchLenBase = posState * LengthSymbols;
        int from = 2;
        for (int k = 0; k < matchCount; k++)
        {
            int ml = Math.Min(_matches[k].Length, maxLen);
            if (ml < from)
            {
                continue;
            }

            uint d0 = (uint)_matches[k].Distance - 1;
            if (ml >= nice)
            {
                Relax(cur + ml, matchBase + _lenPrice[matchLenBase + ml - 2] + DistancePrice(d0, ml), cur, KindMatch, d0, newMatchState, d0, r0, r1, r2, true);
                return true;
            }

            for (int len = from; len <= ml; len++)
            {
                Relax(cur + len, matchBase + _lenPrice[matchLenBase + len - 2] + DistancePrice(d0, len), cur, KindMatch, d0, newMatchState, d0, r0, r1, r2, false);
            }

            from = ml + 1;
        }

        return false;
    }

    private void Relax(int to, int price, int from, byte kind, uint dist, int state, uint r0, uint r1, uint r2, uint r3, bool force)
    {
        while (_end < to)
        {
            _end++;
            _opt[_end].Price = Infinity;
        }

        if (force)
        {
            _end = to;
        }
        else if (price >= _opt[to].Price)
        {
            return;
        }

        ref Node node = ref _opt[to];
        node.Price = price;
        node.Prev = from;
        node.Kind = kind;
        node.Dist = dist;
        node.State = state;
        node.R0 = r0;
        node.R1 = r1;
        node.R2 = r2;
        node.R3 = r3;
    }

    private int DistancePrice(uint distance, int length)
    {
        int lenState = Math.Min(length - LzmaModel.MinMatchLength, LzmaModel.LengthToPositionStates - 1);
        if (distance < 128)
        {
            return _distPrice[(lenState << 7) + (int)distance];
        }

        int slot = GetSlot(distance);
        return _slotPrice[(lenState << 6) + slot] + ((((slot >> 1) - 1) - LzmaModel.AlignBits) * LzmaPrices.UnitsPerBit) + _alignPrice[distance & 15];
    }

    private int LiteralPrice(int p, int state, uint rep0)
    {
        int lit = LiteralBase(p);
        int symbol = _buf[p];
        ushort[] probs = _probs;
        int price = 0;
        int m = 1;
        if (state < LzmaModel.LiteralStateLimit)
        {
            for (int i = 7; i >= 0; i--)
            {
                int bit = (symbol >> i) & 1;
                price += LzmaPrices.Get(probs[lit + m], bit);
                m = (m << 1) | bit;
            }

            return price;
        }

        int matchByte = _buf[p - (int)rep0 - 1];
        bool same = true;
        for (int i = 7; i >= 0; i--)
        {
            int bit = (symbol >> i) & 1;
            if (same)
            {
                int matchBit = (matchByte >> i) & 1;
                price += LzmaPrices.Get(probs[lit + ((1 + matchBit) << 8) + m], bit);
                same = matchBit == bit;
            }
            else
            {
                price += LzmaPrices.Get(probs[lit + m], bit);
            }

            m = (m << 1) | bit;
        }

        return price;
    }

    private int TreePrice(int offset, int bits, int symbol)
    {
        int m = 1;
        int price = 0;
        for (int i = bits - 1; i >= 0; i--)
        {
            int bit = (symbol >> i) & 1;
            price += LzmaPrices.Get(_probs[offset + m], bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    private int ReverseTreePrice(int offset, int bits, int symbol)
    {
        int m = 1;
        int price = 0;
        for (int i = 0; i < bits; i++)
        {
            int bit = symbol & 1;
            symbol >>= 1;
            price += LzmaPrices.Get(_probs[offset + m], bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    // Rebuilds the distance and length price tables from the current probabilities. Doing it every few dozen symbols rather than on
    // every use keeps the parser fast; the schedule depends only on the symbols coded so far, so it is deterministic.
    private void UpdatePrices()
    {
        _priceAge = 0;
        for (int ls = 0; ls < LzmaModel.LengthToPositionStates; ls++)
        {
            int slotOffset = LzmaModel.PositionSlot + (ls << 6);
            for (int slot = 0; slot < 64; slot++)
            {
                _slotPrice[(ls << 6) + slot] = TreePrice(slotOffset, 6, slot);
            }

            for (int d = 0; d < 128; d++)
            {
                int slot = GetSlot((uint)d);
                int price = _slotPrice[(ls << 6) + slot];
                if (slot >= 4)
                {
                    int footer = (slot >> 1) - 1;
                    int baseValue = (2 | (slot & 1)) << footer;
                    price += ReverseTreePrice(LzmaModel.PositionSpecial + baseValue - slot, footer, d - baseValue);
                }

                _distPrice[(ls << 7) + d] = price;
            }
        }

        for (int i = 0; i < 16; i++)
        {
            _alignPrice[i] = ReverseTreePrice(LzmaModel.Align, LzmaModel.AlignBits, i);
        }

        Span<int> low = stackalloc int[8];
        Span<int> mid = stackalloc int[8];
        Span<int> high = stackalloc int[256];
        for (int coderIndex = 0; coderIndex < 2; coderIndex++)
        {
            int coder = coderIndex == 0 ? LzmaModel.LengthCoder : LzmaModel.RepLengthCoder;
            int choice = _probs[coder];
            int choice2 = _probs[coder + LzmaModel.LengthChoice2];
            for (int i = 0; i < 256; i++)
            {
                high[i] = TreePrice(coder + LzmaModel.LengthHigh, 8, i);
            }

            for (int ps = 0; ps <= _pbMask; ps++)
            {
                for (int i = 0; i < 8; i++)
                {
                    low[i] = TreePrice(coder + LzmaModel.LengthLow + (ps << 3), 3, i);
                    mid[i] = TreePrice(coder + LzmaModel.LengthMid + (ps << 3), 3, i);
                }

                int baseIndex = ((coderIndex * LzmaModel.MaxPositionStates) + ps) * LengthSymbols;
                int lowBase = LzmaPrices.Zero(choice);
                int midBase = LzmaPrices.One(choice) + LzmaPrices.Zero(choice2);
                int highBase = LzmaPrices.One(choice) + LzmaPrices.One(choice2);
                for (int l = 0; l < 8; l++)
                {
                    _lenPrice[baseIndex + l] = lowBase + low[l];
                    _lenPrice[baseIndex + 8 + l] = midBase + mid[l];
                }

                for (int l = 16; l < LengthSymbols; l++)
                {
                    _lenPrice[baseIndex + l] = highBase + high[l - 16];
                }
            }
        }
    }

    // The price of a match of `length` at distance `distance0` (the minus-one form), as a fresh symbol in the given state.
    private int MatchPrice(int length, uint distance0, int state, int posState)
    {
        ushort[] probs = _probs;
        return LzmaPrices.One(probs[LzmaModel.IsMatch + (state << 4) + posState]) + LzmaPrices.Zero(probs[LzmaModel.IsRep + state])
            + _lenPrice[(posState * LengthSymbols) + length - 2] + DistancePrice(distance0, length);
    }

    // The price of a repeated match with repeated-distance index `index` of `length`.
    private int RepPrice(int index, int length, int state, int posState)
    {
        ushort[] probs = _probs;
        int price = LzmaPrices.One(probs[LzmaModel.IsMatch + (state << 4) + posState]) + LzmaPrices.One(probs[LzmaModel.IsRep + state]);
        int g0 = probs[LzmaModel.IsRepG0 + state];
        if (index == 0)
        {
            price += LzmaPrices.Zero(g0) + LzmaPrices.One(probs[LzmaModel.IsRep0Long + (state << 4) + posState]);
        }
        else
        {
            int g1 = probs[LzmaModel.IsRepG1 + state];
            price += LzmaPrices.One(g0);
            price += index == 1 ? LzmaPrices.Zero(g1) : LzmaPrices.One(g1) + LzmaPrices.Get(probs[LzmaModel.IsRepG2 + state], index - 2);
        }

        return price + _lenPrice[(((LzmaModel.MaxPositionStates) + posState) * LengthSymbols) + length - 2];
    }
}
