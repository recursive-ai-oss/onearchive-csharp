Inputs that made the 7z reader throw IndexOutOfRangeException before the header checks in docs/7z-reader.md were tightened:
header-0..3,5,6,7 (a second empty-stream vector after the empty-file vector) and header-4,8 (a coder with no input stream).
Seeds for the sevenzip-read fuzz target; the targeted tests are in SevenZipHeaderHardeningTests.
