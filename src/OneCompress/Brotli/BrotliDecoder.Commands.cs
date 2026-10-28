namespace RecursiveAI.OneCompress.Brotli;

public sealed partial class BrotliDecoder
{
    // Where the decoder is within a command (RFC 7932 section 9.3). A command is the insert-and-copy length, the literals it asks for,
    // the distance and then the copy.
    private enum Cmd : byte
    {
        Start,
        Literals,
        Distance,
        Copy,
        Word,
    }

    private Cmd _cmd;
    private int _insertLeft;
    private int _copyLen;
    private bool _implicitDistance;
    private int _copyLeft;
    private int _copyDistance;
    private readonly byte[] _word = new byte[BrotliTransforms.MaxTransformedLength];
    private int _wordLen;
    private int _wordPos;

    private static readonly byte[] s_lut0 = BrotliContextTables.Lut0.ToArray();
    private static readonly byte[] s_lut1 = BrotliContextTables.Lut1.ToArray();
    private static readonly byte[] s_lut2 = BrotliContextTables.Lut2.ToArray();

    // The commands of a compressed meta-block. Returns Ok when the meta-block (and so possibly the stream) is finished.
    private Step RunCommands(ref BrotliBitReader br, Span<byte> dest)
    {
        while (true)
        {
            switch (_cmd)
            {
                case Cmd.Start:
                {
                    if (_metaRemaining == 0)
                    {
                        return EndMetaBlock(ref br);
                    }

                    var checkpoint = br.Save();
                    if (!ReadCommandHeader(ref br))
                    {
                        br.Restore(checkpoint);
                        return Step.Starved;
                    }

                    _cmd = Cmd.Literals;
                    break;
                }

                case Cmd.Literals:
                {
                    Step s = DecodeLiterals(ref br, dest);
                    if (s != Step.Ok)
                    {
                        return s;
                    }

                    // RFC 7932 section 10: when the insertion fills the meta-block the copy length is ignored.
                    if (_metaRemaining == 0)
                    {
                        return EndMetaBlock(ref br);
                    }

                    _cmd = Cmd.Distance;
                    break;
                }

                case Cmd.Distance:
                {
                    var checkpoint = br.Save();
                    if (!ReadDistance(ref br))
                    {
                        br.Restore(checkpoint);
                        return Step.Starved;
                    }

                    break;
                }

                case Cmd.Copy:
                {
                    Step s = DoCopy(dest);
                    if (s != Step.Ok)
                    {
                        return s;
                    }

                    _cmd = Cmd.Start;
                    break;
                }

                default:
                {
                    Step s = DoWord(dest);
                    if (s != Step.Ok)
                    {
                        return s;
                    }

                    _cmd = Cmd.Start;
                    break;
                }
            }
        }
    }

    private Step EndMetaBlock(ref BrotliBitReader br)
    {
        if (!_isLast)
        {
            _phase = Phase.MetaStart;
            return Step.Ok;
        }

        // RFC 7932 section 9.3: the unused bits of the last byte must be zero.
        if (!br.SkipToByteBoundary())
        {
            throw Error(br, "zero bits after the last meta-block");
        }

        br.ReturnWholeBytes();
        _phase = Phase.Done;
        return Step.Ok;
    }

    // Reads, as one unit, an optional block switch for the insert-and-copy lengths and the insert-and-copy length code with its extra
    // bits (RFC 7932 sections 5 and 6).
    private bool ReadCommandHeader(ref BrotliBitReader br)
    {
        int type = _blockType[InsertAndCopy];
        int previous = _blockPrevType[InsertAndCopy];
        int left = _blockLeft[InsertAndCopy];
        if (left == 0 && !SwitchBlock(ref br, InsertAndCopy, ref type, ref previous, ref left))
        {
            return false;
        }

        br.Refill();
        int symbol = br.ReadSymbol(_arena, _insertCodeOffset[type]);
        int insertCode = BrotliCodes.InsertCodeOf[symbol];
        int copyCode = BrotliCodes.CopyCodeOf[symbol];
        br.Refill();
        int insert = BrotliCodes.InsertBase[insertCode] + (int)br.Take(BrotliCodes.InsertExtra[insertCode]);
        br.Refill();
        int copy = BrotliCodes.CopyBase[copyCode] + (int)br.Take(BrotliCodes.CopyExtra[copyCode]);
        if (br.Overrun)
        {
            return false;
        }

        if (insert > _metaRemaining)
        {
            return Bad(br, "an insertion length that fits in the meta-block");
        }

        _blockType[InsertAndCopy] = type;
        _blockPrevType[InsertAndCopy] = previous;
        _blockLeft[InsertAndCopy] = left - 1;
        _insertLeft = insert;
        _copyLen = copy;
        _implicitDistance = symbol < 128;
        return true;
    }

    // RFC 7932 section 6: reads a block switch command - the new block type and the length of the block - into the given variables.
    private bool SwitchBlock(ref BrotliBitReader br, int category, ref int type, ref int previous, ref int left)
    {
        int types = _blockTypes[category];
        if (types == 1)
        {
            // The one block type covers the whole meta-block; the count is only reached after its last element.
            left = int.MaxValue;
            return true;
        }

        br.Refill();
        int symbol = br.ReadSymbol(_arena, _blockTypeCodeOffset[category]);
        int newType = symbol switch
        {
            0 => previous,
            1 => type + 1 == types ? 0 : type + 1,
            _ => symbol - 2,
        };
        br.Refill();
        int countSymbol = br.ReadSymbol(_arena, _blockCountCodeOffset[category]);
        br.Refill();
        int count = BrotliCodes.BlockBase[countSymbol] + (int)br.Take(BrotliCodes.BlockExtra[countSymbol]);
        previous = type;
        type = newType;
        left = count;
        return !br.Overrun;
    }

    // The literals of the current command (RFC 7932 section 7.1). Each literal, with the block switch that may precede it, is read
    // whole or not at all.
    private Step DecodeLiterals(ref BrotliBitReader br, Span<byte> dest)
    {
        uint[] arena = _arena;
        byte[] contextMap = _literalContextMap;
        int[] codeOffset = _literalCodeOffset;
        byte[] lut0 = s_lut0;
        byte[] lut1 = s_lut1;
        byte[] lut2 = s_lut2;
        while (_insertLeft > 0)
        {
            int n = Claim(dest, _insertLeft);
            if (n == 0)
            {
                return Step.Full;
            }

            byte[] ring = _ring;
            int pos = _pos;
            int p1 = _p1;
            int p2 = _p2;
            int type = _blockType[Literals];
            int previous = _blockPrevType[Literals];
            int left = _blockLeft[Literals];
            int done = 0;
            bool starved = false;
            while (done < n)
            {
                var checkpoint = br.Save();
                int t = type;
                int pt = previous;
                int l = left;
                if (l == 0 && !SwitchBlock(ref br, Literals, ref t, ref pt, ref l))
                {
                    br.Restore(checkpoint);
                    starved = true;
                    break;
                }

                int context;
                switch (_contextModes[t])
                {
                    case 0:
                        context = p1 & 63;
                        break;
                    case 1:
                        context = p1 >> 2;
                        break;
                    case 2:
                        context = lut0[p1] | lut1[p2];
                        break;
                    default:
                        context = (lut2[p1] << 3) | lut2[p2];
                        break;
                }

                br.Refill();
                int literal = br.ReadSymbol(arena, codeOffset[contextMap[(t << 6) + context]]);
                if (br.Overrun)
                {
                    br.Restore(checkpoint);
                    starved = true;
                    break;
                }

                ring[pos++] = (byte)literal;
                p2 = p1;
                p1 = literal;
                type = t;
                previous = pt;
                left = l - 1;
                done++;
            }

            _pos = pos;
            _p1 = p1;
            _p2 = p2;
            _blockType[Literals] = type;
            _blockPrevType[Literals] = previous;
            _blockLeft[Literals] = left;
            _insertLeft -= done;
            _metaRemaining -= done;
            _totalOut += done;
            if (starved)
            {
                return Step.Starved;
            }
        }

        return Step.Ok;
    }

    // RFC 7932 sections 4, 7.2 and 8: reads the distance of the command (if the insert-and-copy code did not imply it) and decides
    // whether it is a copy from the window or a reference to the static dictionary.
    private bool ReadDistance(ref BrotliBitReader br)
    {
        int distance;
        bool pushes;
        int type = _blockType[Distances];
        int previous = _blockPrevType[Distances];
        int left = _blockLeft[Distances];
        if (_implicitDistance)
        {
            distance = _d1;
            pushes = false;
        }
        else
        {
            if (left == 0 && !SwitchBlock(ref br, Distances, ref type, ref previous, ref left))
            {
                return false;
            }

            // RFC 7932 section 7.2: the context is the copy length, 2, 3, 4 or more.
            int context = Math.Min(_copyLen, 5) - 2;
            br.Refill();
            int symbol = br.ReadSymbol(_arena, _distanceCodeOffset[_distanceContextMap[(type << 2) + context]]);
            if (symbol < 16)
            {
                switch (symbol)
                {
                    case 0:
                        distance = _d1;
                        break;
                    case 1:
                        distance = _d2;
                        break;
                    case 2:
                        distance = _d3;
                        break;
                    case 3:
                        distance = _d4;
                        break;
                    default:
                    {
                        // 4..9: the last distance and 10..15: the second to last, each changed by -1, +1, -2, +2, -3, +3.
                        int baseDistance = symbol < 10 ? _d1 : _d2;
                        int step = (((symbol - 4) % 6) >> 1) + 1;
                        distance = ((symbol - 4) & 1) == 0 ? baseDistance - step : baseDistance + step;
                        break;
                    }
                }

                pushes = symbol != 0;
            }
            else if (symbol < 16 + _directCodes)
            {
                distance = symbol - 15;
                pushes = true;
            }
            else
            {
                // RFC 7932 section 4.
                int postfix = _postfixBits;
                int code = symbol - _directCodes - 16;
                int extraBits = 1 + (code >> (postfix + 1));
                br.Refill();
                int extra = (int)br.Take(extraBits);
                int high = code >> postfix;
                int low = code & ((1 << postfix) - 1);
                int offset = ((2 + (high & 1)) << extraBits) - 4;
                distance = (((offset + extra) << postfix) + low + _directCodes) + 1;
                pushes = true;
            }
        }

        if (br.Overrun)
        {
            return false;
        }

        if (distance <= 0)
        {
            return Bad(br, "a distance above zero");
        }

        long maxDistance = Math.Min(_window, _totalOut);
        _blockType[Distances] = type;
        _blockPrevType[Distances] = previous;
        _blockLeft[Distances] = _implicitDistance ? left : left - 1;
        if (distance <= maxDistance)
        {
            if (_copyLen > _metaRemaining)
            {
                return Bad(br, "a copy length that fits in the meta-block");
            }

            if (pushes)
            {
                _d4 = _d3;
                _d3 = _d2;
                _d2 = _d1;
                _d1 = distance;
            }

            _copyLeft = _copyLen;
            _copyDistance = distance;
            _cmd = Cmd.Copy;
            return true;
        }

        // The distance reaches beyond the window and the data produced: a word of the static dictionary (RFC 7932 section 8). Such a
        // distance is not entered among the last distances.
        int length = _copyLen;
        if (length < BrotliWords.MinWordLength || length > BrotliWords.MaxWordLength)
        {
            return Bad(br, "a copy length of 4 to 24 for a static dictionary reference");
        }

        long wordId = distance - maxDistance - 1;
        int wordBits = BrotliWords.GetWordBits(length);
        int transform = (int)(wordId >> wordBits);
        if (transform >= BrotliTransforms.Count)
        {
            return Bad(br, "a static dictionary reference with a transform below 121");
        }

        int index = (int)(wordId & ((1L << wordBits) - 1));
        int transformed = BrotliTransforms.ApplyUnchecked(transform, BrotliWords.GetWordUnchecked(length, index), _word);
        if (transformed > _metaRemaining)
        {
            return Bad(br, "a dictionary word that fits in the meta-block");
        }

        _wordLen = transformed;
        _wordPos = 0;
        _cmd = Cmd.Word;
        return true;
    }

    // RFC 7932 section 10: copies from the window, in pieces that fit the output and the end of the ring.
    private Step DoCopy(Span<byte> dest)
    {
        while (_copyLeft > 0)
        {
            int n = Claim(dest, _copyLeft);
            if (n == 0)
            {
                return Step.Full;
            }

            CopyWithin(_copyDistance, n);
            AfterWrite(n);
            _copyLeft -= n;
            _metaRemaining -= n;
            _totalOut += n;
        }

        return Step.Ok;
    }

    // Copies n bytes from distance bytes back, where the source may overlap what is being written (and then repeats it) and may wrap
    // around the end of the ring. The destination lies wholly before the end of the ring.
    private void CopyWithin(int distance, int n)
    {
        byte[] ring = _ring;
        int cap = _cap;
        int pos = _pos;
        int source = pos - distance;
        if (source < 0)
        {
            source += cap;
        }

        int remaining = n;
        while (remaining > 0)
        {
            int segment = Math.Min(remaining, cap - source);
            if (distance >= segment)
            {
                Array.Copy(ring, source, ring, pos, segment);
            }
            else if (distance == 1)
            {
                ring.AsSpan(pos, segment).Fill(ring[source]);
            }
            else
            {
                // The copy repeats its own output with period distance: write one period, then double what is there.
                Array.Copy(ring, source, ring, pos, distance);
                int filled = distance;
                while (filled < segment)
                {
                    int chunk = Math.Min(filled, segment - filled);
                    Array.Copy(ring, pos, ring, pos + filled, chunk);
                    filled += chunk;
                }
            }

            pos += segment;
            source += segment;
            if (source == cap)
            {
                source = 0;
            }

            remaining -= segment;
        }

        _pos = pos;
    }

    // Writes out the transformed dictionary word.
    private Step DoWord(Span<byte> dest)
    {
        while (_wordPos < _wordLen)
        {
            int n = Claim(dest, _wordLen - _wordPos);
            if (n == 0)
            {
                return Step.Full;
            }

            _word.AsSpan(_wordPos, n).CopyTo(_ring.AsSpan(_pos, n));
            _pos += n;
            _wordPos += n;
            _metaRemaining -= n;
            _totalOut += n;
            AfterWrite(n);
        }

        return Step.Ok;
    }
}
