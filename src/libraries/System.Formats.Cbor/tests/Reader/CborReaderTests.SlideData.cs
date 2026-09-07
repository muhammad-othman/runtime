// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Test.Cryptography;
using Xunit;

namespace System.Formats.Cbor.Tests
{
    public partial class CborReaderTests
    {
        private const string UnexpectedEndMessageSentinel = "Unexpected end";

        private static CborReaderOptions LaxOptions => new CborReaderOptions { ConformanceMode = CborConformanceMode.Lax };

        public static IEnumerable<object[]> SampleValuesAndChunkSizes =>
            from hexEncoding in SampleCborValues
            from chunkSize in new[] { 1, 2, 3, 10 }
            select new object[] { hexEncoding, chunkSize };

        // All InvalidCborValues entries that are merely truncated, i.e. could be completed by further data.
        public static IEnumerable<object[]> TruncatedCborInputs =>
            InvalidCborValues.Except(new[] { "bf01ff", "daffffffffff" }).Select(x => new object[] { x });

        [Theory]
        [MemberData(nameof(SampleValuesAndChunkSizes))]
        public static void SlideData_ChunkedReading_HappyPath(string hexEncoding, int chunkSize)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            List<string> expectedTokens = ReadAllTokens(new CborReader(encoding, LaxOptions));

            var feeder = new ChunkedFeeder(encoding, chunkSize);
            var reader = new CborReader(ReadOnlyMemory<byte>.Empty, LaxOptions, isFinalBlock: false);
            List<string> actualTokens = ReadAllTokens(reader, () => feeder.Slide(reader));

            Assert.Equal(expectedTokens, actualTokens);
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Theory]
        [MemberData(nameof(EncodedValueInputs))]
        public static void SlideData_AllSplitPoints_HappyPath(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            List<string> expectedTokens = ReadAllTokens(new CborReader(encoding, LaxOptions));

            for (int split = 0; split < encoding.Length; split++)
            {
                int currentSplit = split;
                var reader = new CborReader(encoding.AsMemory(0, split), LaxOptions, isFinalBlock: false);
                List<string> actualTokens = ReadAllTokens(reader, () =>
                {
                    // slide in the remainder of the document, starting at the reader's current position
                    reader.SlideData(encoding.AsMemory(currentSplit - reader.BytesRemaining), isFinalBlock: true);
                    return true;
                });

                Assert.Equal(expectedTokens, actualTokens);
                Assert.Equal(CborReaderState.Finished, reader.PeekState());
            }
        }

        [Fact]
        public static void SlideData_PreservesNestingContext()
        {
            byte[] encoding = "8301820203820405".HexToByteArray(); // [1, [2, 3], [4, 5]]

            var reader = new CborReader(encoding.AsMemory(0, 4), LaxOptions, isFinalBlock: false);
            reader.ReadStartArray();
            Helpers.VerifyValue(reader, 1);
            reader.ReadStartArray();
            Helpers.VerifyValue(reader, 2);

            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());
            Assert.Equal(2, reader.CurrentDepth);

            reader.SlideData(encoding.AsMemory(4), isFinalBlock: true);

            Assert.Equal(2, reader.CurrentDepth);
            Helpers.VerifyValue(reader, 3);
            reader.ReadEndArray();
            Helpers.VerifyValue(reader, new object[] { 4, 5 });
            reader.ReadEndArray();
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Fact]
        public static void Reset_NotFinalBlock_ResetsNestingContext()
        {
            byte[] encoding = "8301820203820405".HexToByteArray(); // [1, [2, 3], [4, 5]]

            var reader = new CborReader(encoding.AsMemory(0, 4), LaxOptions, isFinalBlock: false);
            reader.ReadStartArray();
            Helpers.VerifyValue(reader, 1);
            reader.ReadStartArray();

            reader.Reset(encoding, isFinalBlock: false);

            Assert.Equal(0, reader.CurrentDepth);
            Assert.Equal(encoding.Length, reader.BytesRemaining);
            Helpers.VerifyValue(reader, new object[] { 1, new object[] { 2, 3 }, new object[] { 4, 5 } });

            // the single root-level value has been consumed, so the document is complete
            // even though the reader has not been handed a final block
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Theory]
        // truncated tokens
        [InlineData("", CborReaderState.NeedsMoreData)]
        [InlineData("18", CborReaderState.NeedsMoreData)] // uint, missing 8-bit argument
        [InlineData("1b00000000", CborReaderState.NeedsMoreData)] // uint, truncated 64-bit argument
        [InlineData("3affff", CborReaderState.NeedsMoreData)] // nint, truncated 32-bit argument
        [InlineData("44", CborReaderState.NeedsMoreData)] // byte string, contents missing
        [InlineData("44010203", CborReaderState.NeedsMoreData)] // byte string, truncated contents
        [InlineData("62c3", CborReaderState.NeedsMoreData)] // text string, truncated contents
        [InlineData("5901", CborReaderState.NeedsMoreData)] // byte string, truncated 16-bit length argument
        [InlineData("9905", CborReaderState.NeedsMoreData)] // array, truncated 16-bit length argument
        [InlineData("d9", CborReaderState.NeedsMoreData)] // tag, missing 16-bit argument
        [InlineData("f8", CborReaderState.NeedsMoreData)] // simple value, missing 8-bit argument
        [InlineData("f9ff", CborReaderState.NeedsMoreData)] // half-precision float, truncated argument
        [InlineData("fa000000", CborReaderState.NeedsMoreData)] // single-precision float, truncated argument
        // complete tokens whose contents are separate tokens
        [InlineData("82", CborReaderState.StartArray)]
        [InlineData("a2", CborReaderState.StartMap)]
        [InlineData("c2", CborReaderState.Tag)]
        [InlineData("5f", CborReaderState.StartIndefiniteLengthByteString)]
        [InlineData("7f", CborReaderState.StartIndefiniteLengthTextString)]
        // malformed initial byte is not a truncation condition
        [InlineData("1c", CborReaderState.UnsignedInteger)] // reserved additional information value
        public static void PeekState_NotFinalBlock_ReturnsExpectedState(string hexEncoding, CborReaderState expectedState)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);
            Assert.Equal(expectedState, reader.PeekState());
            Assert.Equal(encoding.Length, reader.BytesRemaining);
        }

        [Fact]
        public static void PeekState_FinalBlock_EndOfBuffer_ShouldThrowCborContentException()
        {
            var reader = new CborReader(ReadOnlyMemory<byte>.Empty, LaxOptions, isFinalBlock: true);
            Assert.Throws<CborContentException>(() => reader.PeekState());
        }

        [Theory]
        [InlineData("18", CborReaderState.UnsignedInteger)]
        [InlineData("44010203", CborReaderState.ByteString)]
        public static void PeekState_FinalBlock_TruncatedToken_DoesNotGateReads(string hexEncoding, CborReaderState expectedState)
        {
            // final-block readers preserve the historical behavior: PeekState validates
            // the initial byte only, and truncation surfaces when the token is read
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: true);
            Assert.Equal(expectedState, reader.PeekState());
            Assert.Throws<CborContentException>(() => reader.SkipValue());
        }

        [Theory]
        [InlineData("ff")] // break byte at the root context
        [InlineData("c2ff")] // tag followed by break byte
        [InlineData("bf01ff")] // indefinite-length map key missing a value
        [InlineData("5f01ff")] // indefinite-length byte string containing an invalid data item
        public static void PeekState_NotFinalBlock_MalformedData_ShouldThrowCborContentException(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);
            Assert.Throws<CborContentException>(() => ReadAllTokens(reader));
        }

        [Fact]
        public static void PeekState_NotFinalBlock_StateIsRefreshedAfterSlideData()
        {
            var reader = new CborReader(ReadOnlyMemory<byte>.Empty, LaxOptions, isFinalBlock: false);
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());

            reader.SlideData("01".HexToByteArray(), isFinalBlock: true);
            Assert.Equal(CborReaderState.UnsignedInteger, reader.PeekState());
        }

        [Fact]
        public static void PeekState_NotFinalBlock_MultipleRootLevelValues_ReturnsNeedsMoreDataBetweenValues()
        {
            var options = new CborReaderOptions { ConformanceMode = CborConformanceMode.Lax, AllowMultipleRootLevelValues = true };
            var reader = new CborReader(ReadOnlyMemory<byte>.Empty, options, isFinalBlock: false);
            byte[] encoding = "010203".HexToByteArray();

            for (int i = 0; i < encoding.Length; i++)
            {
                Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());
                reader.SlideData(encoding.AsMemory(i, 1), isFinalBlock: false);
                Helpers.VerifyValue(reader, i + 1);
            }

            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());
            reader.SlideData(ReadOnlyMemory<byte>.Empty, isFinalBlock: true);
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Theory]
        [MemberData(nameof(TruncatedCborInputs))]
        public static void TrySkipValue_NotFinalBlock_TruncatedValue_ReturnsFalseAndPreservesState(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);

            Assert.False(reader.TrySkipValue());
            Assert.Equal(encoding.Length, reader.BytesRemaining);
            Assert.Equal(0, reader.CurrentDepth);
        }

        [Theory]
        [MemberData(nameof(EncodedValueInputs))]
        public static void TrySkipValue_NotFinalBlock_AllSplitPoints_SucceedsAfterSlideData(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();

            for (int split = 0; split < encoding.Length; split++)
            {
                var reader = new CborReader(encoding.AsMemory(0, split), LaxOptions, isFinalBlock: false);

                Assert.False(reader.TrySkipValue());
                Assert.Equal(split, reader.BytesRemaining); // reader state was restored

                reader.SlideData(encoding, isFinalBlock: true);
                Assert.True(reader.TrySkipValue());
                Assert.Equal(CborReaderState.Finished, reader.PeekState());
            }
        }

        [Theory]
        [MemberData(nameof(EncodedValueInputs))]
        public static void TrySkipToParent_NotFinalBlock_AllSplitPoints_SucceedsAfterSlideData(string hexEncoding)
        {
            byte[] encoding = ("8301" + hexEncoding + "03").HexToByteArray(); // [1, <value>, 3]

            for (int split = 2; split < encoding.Length; split++)
            {
                var reader = new CborReader(encoding.AsMemory(0, split), LaxOptions, isFinalBlock: false);
                reader.ReadStartArray();
                Helpers.VerifyValue(reader, 1);

                int bytesRemaining = reader.BytesRemaining;
                Assert.False(reader.TrySkipToParent());
                Assert.Equal(bytesRemaining, reader.BytesRemaining); // reader state was restored
                Assert.Equal(1, reader.CurrentDepth);

                reader.SlideData(encoding.AsMemory(split - reader.BytesRemaining), isFinalBlock: true);
                Assert.True(reader.TrySkipToParent());
                Assert.Equal(0, reader.CurrentDepth);
                Assert.Equal(CborReaderState.Finished, reader.PeekState());
            }
        }

        [Theory]
        [MemberData(nameof(SkipTestInputs))]
        public static void TrySkipValue_FinalBlock_HappyPath(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding);

            Assert.True(reader.TrySkipValue());
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Theory]
        [MemberData(nameof(SkipTestInvalidCborInputs))]
        public static void TrySkipValue_FinalBlock_InvalidValue_ShouldThrowCborContentException(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding);

            Assert.Throws<CborContentException>(() => reader.TrySkipValue());
            Assert.Equal(encoding.Length, reader.BytesRemaining);
        }

        [Theory]
        [InlineData("bf01ff")] // indefinite-length map key missing a value
        [InlineData("daffffffffff")] // tag followed by break byte
        [InlineData("1c")] // reserved additional information value
        [InlineData("ff")] // break byte at the root context
        public static void TrySkipValue_NotFinalBlock_MalformedData_ShouldThrowCborContentException(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);

            Assert.Throws<CborContentException>(() => reader.TrySkipValue());
            Assert.Equal(encoding.Length, reader.BytesRemaining);
        }

        [Theory]
        [MemberData(nameof(NonConformingSkipValueEncodings))]
        public static void TrySkipValue_ValidationDisabled_NonConformingValues_ShouldSucceed(CborConformanceMode mode, string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, mode);

            Assert.True(reader.TrySkipValue(disableConformanceModeChecks: true));
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Fact]
        public static void TrySkipToParent_RootContext_ShouldThrowInvalidOperationException()
        {
            var reader = new CborReader("01".HexToByteArray(), LaxOptions, isFinalBlock: false);
            Assert.Throws<InvalidOperationException>(() => reader.TrySkipToParent());
        }

        [Fact]
        public static void TrySkipValue_NotFinalBlock_AfterReadTag_ShouldRestoreTagContext()
        {
            byte[] encoding = "c1820102".HexToByteArray(); // 1([1, 2])
            var reader = new CborReader(encoding.AsMemory(0, 3), LaxOptions, isFinalBlock: false);

            Assert.Equal((CborTag)1, reader.ReadTag());

            // the failed skip enters the truncated array before restoring,
            // which exercises checkpoint restoration of the pending tag context
            Assert.False(reader.TrySkipValue());
            Assert.Equal(2, reader.BytesRemaining);
            Assert.Equal(CborReaderState.StartArray, reader.PeekState());

            reader.SlideData(encoding.AsMemory(1), isFinalBlock: true);
            Assert.True(reader.TrySkipValue());
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Fact]
        public static void TrySkipValue_NotFinalBlock_TruncatedNestedTags_ShouldRestoreTagContext()
        {
            byte[] encoding = "c1c201".HexToByteArray(); // 1(2(1))
            var reader = new CborReader(encoding.AsMemory(0, 2), LaxOptions, isFinalBlock: false);

            // the failed skip consumes both tags before restoring,
            // which exercises checkpoint restoration of the pending tag context
            Assert.False(reader.TrySkipValue());
            Assert.Equal(2, reader.BytesRemaining);
            Assert.Equal(CborReaderState.Tag, reader.PeekState());

            reader.SlideData(encoding, isFinalBlock: true);
            Assert.True(reader.TrySkipValue());
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Fact]
        public static void ReadEncodedValue_NotFinalBlock_TruncatedValue_ShouldThrowCborContentException()
        {
            byte[] encoding = "820102".HexToByteArray(); // [1, 2]
            var reader = new CborReader(encoding.AsMemory(0, 2), LaxOptions, isFinalBlock: false);

            Assert.Throws<CborContentException>(() => reader.ReadEncodedValue());
            Assert.Equal(2, reader.BytesRemaining);

            reader.SlideData(encoding, isFinalBlock: true);
            Assert.Equal(encoding, reader.ReadEncodedValue().ToArray());
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Fact]
        public static void TryReadByteString_NotFinalBlock_FalseMeansDestinationTooSmallOnly()
        {
            byte[] encoding = "4401020304".HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);

            // a fully buffered value: false signals an undersized destination, not missing data
            Assert.Equal(CborReaderState.ByteString, reader.PeekState());
            Assert.False(reader.TryReadByteString(new byte[2], out int bytesWritten));
            Assert.Equal(0, bytesWritten);
            Assert.Equal(encoding.Length, reader.BytesRemaining);

            Assert.True(reader.TryReadByteString(new byte[4], out bytesWritten));
            Assert.Equal(4, bytesWritten);

            // a truncated value is not reported via the boolean; it throws as in final blocks
            reader = new CborReader("440102".HexToByteArray(), LaxOptions, isFinalBlock: false);
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());
            Assert.Throws<CborContentException>(() => reader.TryReadByteString(new byte[4], out _));
        }

        [Fact]
        public static void TryReadTextString_NotFinalBlock_FalseMeansDestinationTooSmallOnly()
        {
            byte[] encoding = "6461626364".HexToByteArray(); // "abcd"
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);

            // a fully buffered value: false signals an undersized destination, not missing data
            Assert.Equal(CborReaderState.TextString, reader.PeekState());
            Assert.False(reader.TryReadTextString(new char[2], out int charsWritten));
            Assert.Equal(0, charsWritten);
            Assert.Equal(encoding.Length, reader.BytesRemaining);

            Assert.True(reader.TryReadTextString(new char[4], out charsWritten));
            Assert.Equal(4, charsWritten);

            // a truncated value is not reported via the boolean; it throws as in final blocks
            reader = new CborReader("646162".HexToByteArray(), LaxOptions, isFinalBlock: false);
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());
            Assert.Throws<CborContentException>(() => reader.TryReadTextString(new char[4], out _));
        }

        [Theory]
        [MemberData(nameof(TruncatedCborInputs))]
        public static void SkipValue_NotFinalBlock_TruncatedValue_ShouldThrowCborContentException(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);

            Assert.Throws<CborContentException>(() => reader.SkipValue());
            Assert.Equal(encoding.Length, reader.BytesRemaining);
        }

        [Theory]
        [InlineData(CborConformanceMode.Strict)]
        [InlineData(CborConformanceMode.Canonical)]
        [InlineData(CborConformanceMode.Ctap2Canonical)]
        public static void Constructor_NotFinalBlock_NonLaxConformance_ShouldThrowArgumentException(CborConformanceMode mode)
        {
            var options = new CborReaderOptions { ConformanceMode = mode };
            AssertExtensions.Throws<ArgumentException>("isFinalBlock", () => new CborReader(ReadOnlyMemory<byte>.Empty, options, isFinalBlock: false));
        }

        [Fact]
        public static void Constructor_NotFinalBlock_NullOptions_ShouldThrowArgumentException()
        {
            // null options default to the Strict conformance mode
            AssertExtensions.Throws<ArgumentException>("isFinalBlock", () => new CborReader(ReadOnlyMemory<byte>.Empty, null, isFinalBlock: false));
        }

        [Theory]
        [InlineData(CborConformanceMode.Strict)]
        [InlineData(CborConformanceMode.Canonical)]
        [InlineData(CborConformanceMode.Ctap2Canonical)]
        public static void Reset_NotFinalBlock_NonLaxConformance_ShouldThrowArgumentException(CborConformanceMode mode)
        {
            var reader = new CborReader(ReadOnlyMemory<byte>.Empty, new CborReaderOptions { ConformanceMode = mode });
            AssertExtensions.Throws<ArgumentException>("isFinalBlock", () => reader.Reset(ReadOnlyMemory<byte>.Empty, isFinalBlock: false));
        }

        [Fact]
        public static void SlideData_FinalBlock_ShouldThrowInvalidOperationException()
        {
            byte[] encoding = "01".HexToByteArray();

            // default constructors produce final-block readers
            var reader = new CborReader(encoding);
            Assert.Throws<InvalidOperationException>(() => reader.SlideData(encoding, isFinalBlock: false));

            reader = new CborReader(encoding, LaxOptions, isFinalBlock: true);
            Assert.Throws<InvalidOperationException>(() => reader.SlideData(encoding, isFinalBlock: false));

            // sliding in a final block prevents further slides
            reader = new CborReader(ReadOnlyMemory<byte>.Empty, LaxOptions, isFinalBlock: false);
            reader.SlideData(encoding, isFinalBlock: true);
            Assert.Throws<InvalidOperationException>(() => reader.SlideData(encoding, isFinalBlock: false));
        }

        [Fact]
        public static void Reset_SingleArgument_MakesReaderFinalBlock()
        {
            byte[] encoding = "01".HexToByteArray();
            var reader = new CborReader(ReadOnlyMemory<byte>.Empty, LaxOptions, isFinalBlock: false);

            reader.Reset(encoding);
            Assert.Throws<InvalidOperationException>(() => reader.SlideData(encoding, isFinalBlock: false));

            reader.Reset(encoding, isFinalBlock: false);
            reader.SlideData(encoding, isFinalBlock: true); // does not throw
        }

        [Fact]
        public static void ReadStartArray_NotFinalBlock_TruncatedContents_ShouldSucceed()
        {
            byte[] encoding = "990100".HexToByteArray(); // array of declared length 256, contents missing

            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);
            Assert.Equal(256, reader.ReadStartArray());
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());

            // the conservative declared-length check still applies to final blocks
            reader = new CborReader(encoding, LaxOptions, isFinalBlock: true);
            Assert.Throws<CborContentException>(() => reader.ReadStartArray());
        }

        [Fact]
        public static void ReadStartMap_NotFinalBlock_TruncatedContents_ShouldSucceed()
        {
            byte[] encoding = "b90100".HexToByteArray(); // map of declared length 256, contents missing

            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);
            Assert.Equal(256, reader.ReadStartMap());
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());

            // the conservative declared-length check still applies to final blocks
            reader = new CborReader(encoding, LaxOptions, isFinalBlock: true);
            Assert.Throws<CborContentException>(() => reader.ReadStartMap());
        }

        [Theory]
        [InlineData("5b7fffffffffffffff")] // byte string declaring a length that cannot fit any buffer
        [InlineData("9b7fffffffffffffff")] // array declaring a length that cannot fit any buffer
        [InlineData("bb7fffffffffffffff")] // map declaring a length that cannot fit any buffer
        public static void Read_NotFinalBlock_UnrepresentableDefiniteLength_ShouldThrowCborContentException(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);

            // an unrepresentable length is not a truncation condition: the token is reported as available
            Assert.NotEqual(CborReaderState.NeedsMoreData, reader.PeekState());

            Assert.Throws<CborContentException>(() =>
            {
                switch (encoding[0])
                {
                    case 0x5b: reader.ReadByteString(); break;
                    case 0x9b: reader.ReadStartArray(); break;
                    default: reader.ReadStartMap(); break;
                }
            });
            Assert.Equal(encoding.Length, reader.BytesRemaining);

            Assert.Throws<CborContentException>(() => reader.SkipValue());
            Assert.Equal(encoding.Length, reader.BytesRemaining);
        }

        [Theory]
        [InlineData(2, 17)]
        [InlineData(500, 16)]
        public static void SlideData_LargeDefiniteLengthCollections_HappyPath(int length, int chunkSize)
        {
            var writer = new CborWriter();
            writer.WriteStartArray(length);
            for (int i = 0; i < length; i++)
            {
                writer.WriteInt32(i);
            }
            writer.WriteEndArray();
            byte[] arrayEncoding = writer.Encode();

            writer.Reset();
            writer.WriteStartMap(length);
            for (int i = 0; i < length; i++)
            {
                writer.WriteInt32(i);
                writer.WriteTextString($"value{i}");
            }
            writer.WriteEndMap();
            byte[] mapEncoding = writer.Encode();

            foreach (byte[] encoding in new[] { arrayEncoding, mapEncoding })
            {
                List<string> expectedTokens = ReadAllTokens(new CborReader(encoding, LaxOptions));

                var feeder = new ChunkedFeeder(encoding, chunkSize);
                var reader = new CborReader(ReadOnlyMemory<byte>.Empty, LaxOptions, isFinalBlock: false);
                List<string> actualTokens = ReadAllTokens(reader, () => feeder.Slide(reader));

                Assert.Equal(expectedTokens, actualTokens);
            }
        }

        [Theory]
        [InlineData("5f41ab")] // indefinite-length byte string missing chunks and break byte
        [InlineData("5f4401")] // indefinite-length byte string with a truncated chunk
        public static void ReadByteString_NotFinalBlock_TruncatedIndefiniteLengthString_ShouldThrowCborContentException(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);

            // the concatenating read consumes multiple tokens and can fail beyond the peeked token
            Assert.Equal(CborReaderState.StartIndefiniteLengthByteString, reader.PeekState());
            Assert.Throws<CborContentException>(() => reader.ReadByteString());
            Assert.Equal(encoding.Length, reader.BytesRemaining);

            // the non-throwing alternatives report the truncation
            Assert.False(reader.TrySkipValue());
        }

        [Fact]
        public static void SlideData_StreamScenario_HappyPath()
        {
            var writer = new CborWriter();
            writer.WriteStartMap(3);
            writer.WriteTextString("name");
            writer.WriteTextString(new string('x', 100));
            writer.WriteTextString("items");
            writer.WriteStartArray(50);
            for (int i = 0; i < 50; i++)
            {
                writer.WriteInt32(i);
            }
            writer.WriteEndArray();
            writer.WriteTextString("blob");
            writer.WriteByteString(new byte[75]);
            writer.WriteEndMap();
            byte[] encoding = writer.Encode();

            List<string> expectedTokens = ReadAllTokens(new CborReader(encoding, LaxOptions));

            // mirrors the intended usage pattern: fixed-size buffer over a stream,
            // copying the unconsumed tail to the front on every refill.
            using var stream = new MemoryStream(encoding);
            byte[] buffer = new byte[16];
            int dataLength = 0;
            var reader = new CborReader(ReadOnlyMemory<byte>.Empty, LaxOptions, isFinalBlock: false);

            List<string> actualTokens = ReadAllTokens(reader, () =>
            {
                int keep = reader.BytesRemaining;
                int start = dataLength - keep;

                if (keep == buffer.Length)
                {
                    // a single token is larger than the buffer
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                Buffer.BlockCopy(buffer, start, buffer, 0, keep);
                int read = stream.Read(buffer, keep, buffer.Length - keep);
                dataLength = keep + read;

                reader.SlideData(buffer.AsMemory(0, dataLength), isFinalBlock: read == 0);
                return true;
            });

            Assert.Equal(expectedTokens, actualTokens);
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Fact]
        public static void PeekState_DanglingTagAtEndOfRootSequence_ShouldThrowCborContentException()
        {
            var options = new CborReaderOptions { ConformanceMode = CborConformanceMode.Lax, AllowMultipleRootLevelValues = true };

            // non-final mode: the truncated tagged value is reported as NeedsMoreData until the final block arrives
            var reader = new CborReader("01c1".HexToByteArray(), options, isFinalBlock: false);
            Helpers.VerifyValue(reader, 1);
            reader.ReadTag();
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());

            reader.SlideData(ReadOnlyMemory<byte>.Empty, isFinalBlock: true);
            Assert.Throws<CborContentException>(() => reader.PeekState());

            // final mode: the dangling tag surfaces immediately instead of reporting Finished
            reader = new CborReader("01c1".HexToByteArray(), options, isFinalBlock: true);
            Helpers.VerifyValue(reader, 1);
            reader.ReadTag();
            Assert.Throws<CborContentException>(() => reader.PeekState());
        }

        [Fact]
        public static void SlideData_CompletesTaggedValueAtEndOfRootSequence()
        {
            var options = new CborReaderOptions { ConformanceMode = CborConformanceMode.Lax, AllowMultipleRootLevelValues = true };
            var reader = new CborReader("01c1".HexToByteArray(), options, isFinalBlock: false);
            Helpers.VerifyValue(reader, 1);
            Assert.Equal((CborTag)1, reader.ReadTag());
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());

            reader.SlideData("187b".HexToByteArray(), isFinalBlock: true);
            Helpers.VerifyValue(reader, 123);
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Theory]
        [InlineData("c07818323032302d30342d31305431303a30353a33372e3939395a")] // 0("2020-04-10T10:05:37.999Z")
        [InlineData("c11a514b67b0")] // 1(1363896240)
        [InlineData("c249010000000000000000")] // 2(h'010000000000000000')
        [InlineData("c48221196ab3")] // 4([-2, 27315])
        public static void ReadTaggedValue_NotFinalBlock_AllSplitPoints_SucceedsAfterSlideData(string hexEncoding)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            object expected = ReadTaggedValue(new CborReader(encoding, LaxOptions));

            for (int split = 1; split < encoding.Length; split++)
            {
                var reader = new CborReader(encoding.AsMemory(0, split), LaxOptions, isFinalBlock: false);

                // semantic tag readers consume multiple tokens, so they throw on truncation
                // even though the tag token itself passes the PeekState gate; the message
                // reports the truncation rather than an invalid semantic encoding
                AssertExtensions.ThrowsContains<CborContentException>(() => ReadTaggedValue(reader), UnexpectedEndMessageSentinel);
                Assert.Equal(split, reader.BytesRemaining); // reader state was restored

                reader.SlideData(encoding, isFinalBlock: true);
                Assert.Equal(expected, ReadTaggedValue(reader));
                Assert.Equal(CborReaderState.Finished, reader.PeekState());
            }
        }

        [Fact]
        public static void SlideData_BufferSmallerThanBytesRemaining_ShouldThrowArgumentException()
        {
            byte[] encoding = "820102".HexToByteArray(); // [1, 2]
            var reader = new CborReader(encoding, LaxOptions, isFinalBlock: false);
            reader.ReadStartArray();
            Assert.Equal(2, reader.BytesRemaining);

            AssertExtensions.Throws<ArgumentException>("data", () => reader.SlideData("01".HexToByteArray(), isFinalBlock: false));

            // the failed call leaves the reader unchanged; an equally-sized buffer is accepted
            Assert.Equal(2, reader.BytesRemaining);
            reader.SlideData("0102".HexToByteArray(), isFinalBlock: true);
            Helpers.VerifyValue(reader, 1);
            Helpers.VerifyValue(reader, 2);
            reader.ReadEndArray();
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Fact]
        public static void ReadStartMap_NotFinalBlock_PairCountOverflow_ShouldThrowCborContentException()
        {
            // the key-value item count 2 * mapSize must remain representable even though
            // the contents can be supplied by later SlideData calls
            var reader = new CborReader("ba7fffffff".HexToByteArray(), LaxOptions, isFinalBlock: false); // int.MaxValue pairs
            Assert.Throws<CborContentException>(() => reader.ReadStartMap());

            reader = new CborReader("ba40000000".HexToByteArray(), LaxOptions, isFinalBlock: false); // int.MaxValue / 2 + 1 pairs
            Assert.Throws<CborContentException>(() => reader.ReadStartMap());

            reader = new CborReader("ba3fffffff".HexToByteArray(), LaxOptions, isFinalBlock: false); // int.MaxValue / 2 pairs
            Assert.Equal(int.MaxValue / 2, reader.ReadStartMap());
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());
        }

        [Theory]
        [MemberData(nameof(SampleValuesAndChunkSizes))]
        public static void TrySkipValue_NotFinalBlock_ChunkedReading_HappyPath(string hexEncoding, int chunkSize)
        {
            byte[] encoding = hexEncoding.HexToByteArray();
            var feeder = new ChunkedFeeder(encoding, chunkSize);
            var reader = new CborReader(ReadOnlyMemory<byte>.Empty, LaxOptions, isFinalBlock: false);

            while (!reader.TrySkipValue())
            {
                feeder.Slide(reader);
            }

            Assert.Equal(0, reader.BytesRemaining);
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Fact]
        public static void TrySkipToParent_NotFinalBlock_NestedContexts_SucceedsAfterSlideData()
        {
            byte[] encoding = "a16161820102".HexToByteArray(); // {"a": [1, 2]}

            for (int split = 4; split < encoding.Length; split++)
            {
                var reader = new CborReader(encoding.AsMemory(0, split), LaxOptions, isFinalBlock: false);
                reader.ReadStartMap();
                reader.ReadTextString();
                reader.ReadStartArray();
                Assert.Equal(2, reader.CurrentDepth);

                Assert.False(reader.TrySkipToParent());
                Assert.Equal(2, reader.CurrentDepth);

                reader.SlideData(encoding.AsMemory(split - reader.BytesRemaining), isFinalBlock: true);
                Assert.True(reader.TrySkipToParent()); // exits the array
                Assert.Equal(1, reader.CurrentDepth);
                Assert.True(reader.TrySkipToParent()); // exits the map
                Assert.Equal(0, reader.CurrentDepth);
                Assert.Equal(CborReaderState.Finished, reader.PeekState());
            }
        }

        [Fact]
        public static void SlideData_MaxDepthLimit_EnforcedAcrossSlides()
        {
            var options = new CborReaderOptions { ConformanceMode = CborConformanceMode.Lax, MaxDepth = 2 };
            var reader = new CborReader("81".HexToByteArray(), options, isFinalBlock: false);
            reader.ReadStartArray();

            reader.SlideData("81".HexToByteArray(), isFinalBlock: false);
            reader.ReadStartArray();

            reader.SlideData("81".HexToByteArray(), isFinalBlock: false);
            Assert.Equal(CborReaderState.StartArray, reader.PeekState());
            Assert.Throws<CborContentException>(() => reader.ReadStartArray());
        }

        [Fact]
        public static void ReadInt32_NotFinalBlock_TruncatedArgument_ThrowsAndRecoversAfterSlideData()
        {
            var reader = new CborReader("1a000f".HexToByteArray(), LaxOptions, isFinalBlock: false);

            // reading past NeedsMoreData throws, but leaves the reader state unchanged
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());
            Assert.Throws<CborContentException>(() => reader.ReadInt32());
            Assert.Equal(3, reader.BytesRemaining);

            reader.SlideData("1a000f4240".HexToByteArray(), isFinalBlock: true);
            Assert.Equal(1_000_000, reader.ReadInt32());
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }

        [Theory]
        [InlineData(CborConformanceMode.Lax)]
        [InlineData(CborConformanceMode.Strict)]
        [InlineData(CborConformanceMode.Canonical)]
        [InlineData(CborConformanceMode.Ctap2Canonical)]
        public static void Constructor_FinalBlock_AnyConformanceMode_Succeeds(CborConformanceMode mode)
        {
            var options = new CborReaderOptions { ConformanceMode = mode };
            var reader = new CborReader("01".HexToByteArray(), options, isFinalBlock: true);
            Helpers.VerifyValue(reader, 1);

            reader.Reset("01".HexToByteArray(), isFinalBlock: true);
            Helpers.VerifyValue(reader, 1);
        }

        [Fact]
        public static void Read_NotFinalBlock_AtRootValueBoundary_ShouldThrowCborContentException()
        {
            var options = new CborReaderOptions { ConformanceMode = CborConformanceMode.Lax, AllowMultipleRootLevelValues = true };
            var reader = new CborReader("01".HexToByteArray(), options, isFinalBlock: false);
            Helpers.VerifyValue(reader, 1);
            Assert.Equal(CborReaderState.NeedsMoreData, reader.PeekState());

            // reading past NeedsMoreData reports the truncation, mirroring mid-token truncation,
            // rather than declaring the end of the sequence
            AssertExtensions.ThrowsContains<CborContentException>(() => reader.ReadInt32(), UnexpectedEndMessageSentinel);

            reader.SlideData("02".HexToByteArray(), isFinalBlock: false);
            Helpers.VerifyValue(reader, 2);
        }

        [Fact]
        public static void Read_DanglingTagAtEndOfRootSequence_ShouldThrowCborContentException()
        {
            var options = new CborReaderOptions { ConformanceMode = CborConformanceMode.Lax, AllowMultipleRootLevelValues = true };
            var reader = new CborReader("01c1".HexToByteArray(), options, isFinalBlock: true);
            Helpers.VerifyValue(reader, 1);
            reader.ReadTag();

            // direct reads report the dangling tag like PeekState does,
            // rather than declaring a clean end of the sequence
            Assert.Throws<CborContentException>(() => reader.ReadInt32());
        }

        [Fact]
        public static void Read_FinishedRootSequenceAfterEmptyFinalSlide_ShouldThrowInvalidOperationException()
        {
            var options = new CborReaderOptions { ConformanceMode = CborConformanceMode.Lax, AllowMultipleRootLevelValues = true };
            var reader = new CborReader("01".HexToByteArray(), options, isFinalBlock: false);
            Helpers.VerifyValue(reader, 1);

            reader.SlideData(ReadOnlyMemory<byte>.Empty, isFinalBlock: true);
            Assert.Equal(CborReaderState.Finished, reader.PeekState());

            // over-reading a well-formed, fully consumed sequence is a usage error, not a content error
            Assert.Throws<InvalidOperationException>(() => reader.ReadInt32());
        }

        // Dispatches to the strongly-typed semantic tag reader for the next value's tag.
        private static object ReadTaggedValue(CborReader reader)
        {
            return reader.PeekTag() switch
            {
                CborTag.DateTimeString => reader.ReadDateTimeOffset(),
                CborTag.UnixTimeSeconds => reader.ReadUnixTimeSeconds(),
                CborTag.UnsignedBigNum or CborTag.NegativeBigNum => (object)reader.ReadBigInteger(),
                CborTag.DecimalFraction => reader.ReadDecimal(),
                _ => throw new InvalidOperationException($"Unexpected tag {reader.PeekTag()}."),
            };
        }

        // Walks an entire document token by token, gated on PeekState(),
        // invoking the supplied callback whenever the reader needs more data.
        // Returns a trace of the tokens read for comparison across buffering strategies.
        private static List<string> ReadAllTokens(CborReader reader, Func<bool>? slide = null)
        {
            var tokens = new List<string>();

            while (true)
            {
                CborReaderState state = reader.PeekState();
                switch (state)
                {
                    case CborReaderState.Finished:
                        return tokens;
                    case CborReaderState.NeedsMoreData:
                        Assert.NotNull(slide);
                        Assert.True(slide!(), "Reader needs more data, but the full document has already been supplied.");
                        continue;
                    case CborReaderState.UnsignedInteger:
                        tokens.Add($"{state}:{reader.ReadUInt64()}");
                        break;
                    case CborReaderState.NegativeInteger:
                        tokens.Add($"{state}:{reader.ReadCborNegativeIntegerRepresentation()}");
                        break;
                    case CborReaderState.ByteString:
                        tokens.Add($"{state}:{reader.ReadByteString().ByteArrayToHex()}");
                        break;
                    case CborReaderState.TextString:
                        tokens.Add($"{state}:{reader.ReadTextString()}");
                        break;
                    case CborReaderState.StartIndefiniteLengthByteString:
                        reader.ReadStartIndefiniteLengthByteString();
                        tokens.Add(state.ToString());
                        break;
                    case CborReaderState.EndIndefiniteLengthByteString:
                        reader.ReadEndIndefiniteLengthByteString();
                        tokens.Add(state.ToString());
                        break;
                    case CborReaderState.StartIndefiniteLengthTextString:
                        reader.ReadStartIndefiniteLengthTextString();
                        tokens.Add(state.ToString());
                        break;
                    case CborReaderState.EndIndefiniteLengthTextString:
                        reader.ReadEndIndefiniteLengthTextString();
                        tokens.Add(state.ToString());
                        break;
                    case CborReaderState.StartArray:
                        tokens.Add($"{state}:{reader.ReadStartArray()}");
                        break;
                    case CborReaderState.EndArray:
                        reader.ReadEndArray();
                        tokens.Add(state.ToString());
                        break;
                    case CborReaderState.StartMap:
                        tokens.Add($"{state}:{reader.ReadStartMap()}");
                        break;
                    case CborReaderState.EndMap:
                        reader.ReadEndMap();
                        tokens.Add(state.ToString());
                        break;
                    case CborReaderState.Tag:
                        tokens.Add($"{state}:{reader.ReadTag()}");
                        break;
                    case CborReaderState.HalfPrecisionFloat:
                    case CborReaderState.SinglePrecisionFloat:
                    case CborReaderState.DoublePrecisionFloat:
                        tokens.Add($"{state}:{reader.ReadDouble()}");
                        break;
                    case CborReaderState.Null:
                        reader.ReadNull();
                        tokens.Add(state.ToString());
                        break;
                    case CborReaderState.Boolean:
                        tokens.Add($"{state}:{reader.ReadBoolean()}");
                        break;
                    case CborReaderState.SimpleValue:
                        tokens.Add($"{state}:{reader.ReadSimpleValue()}");
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected reader state {state}.");
                }
            }
        }

        // Supplies a document to a non-final-block reader in fixed-size chunks,
        // copying the unconsumed tail to the front of the buffer on every slide.
        private sealed class ChunkedFeeder
        {
            private readonly byte[] _encoding;
            private readonly int _chunkSize;
            private int _position;
            private byte[] _buffer;
            private int _dataLength;

            public ChunkedFeeder(byte[] encoding, int chunkSize)
            {
                _encoding = encoding;
                _chunkSize = chunkSize;
                _buffer = new byte[chunkSize];
            }

            public bool Slide(CborReader reader)
            {
                Assert.True(_position < _encoding.Length, "Reader needs more data, but the full document has already been supplied.");

                int keep = reader.BytesRemaining;
                int start = _dataLength - keep;
                int read = Math.Min(_chunkSize, _encoding.Length - _position);

                if (_buffer.Length < keep + read)
                {
                    Array.Resize(ref _buffer, Math.Max(2 * _buffer.Length, keep + read));
                }

                Buffer.BlockCopy(_buffer, start, _buffer, 0, keep);
                Buffer.BlockCopy(_encoding, _position, _buffer, keep, read);
                _position += read;
                _dataLength = keep + read;

                reader.SlideData(_buffer.AsMemory(0, _dataLength), isFinalBlock: _position == _encoding.Length);
                return true;
            }
        }
    }
}
