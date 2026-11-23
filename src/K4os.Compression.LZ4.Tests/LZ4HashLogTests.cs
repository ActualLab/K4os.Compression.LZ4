using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4.Encoders;
using Xunit;
using Xunit.Abstractions;

namespace K4os.Compression.LZ4.Tests
{
	public class LZ4HashLogTests
	{
		private const int BlockSize = 16 * 1024;

		private readonly ITestOutputHelper _output;

		public LZ4HashLogTests(ITestOutputHelper output)
		{
			_output = output;
			LZ4Codec.Enforce32 = false;
		}

		public static IEnumerable<object[]> RoundtripCases() =>
			from hashLog in new[] { 10, 12, 14, 16 }
			from extraBlocks in new[] { 0, 4 }
			from withDictionary in new[] { false, true }
			select new object[] { hashLog, extraBlocks, withDictionary };

		[Theory]
		[MemberData(nameof(RoundtripCases))]
		public void Roundtrip(int hashLog, int extraBlocks, bool withDictionary)
		{
			HashLogTestKit.AssertRoundtrip(hashLog, extraBlocks, withDictionary);
		}

		[Fact]
		public void DefaultOutputShouldNotChange()
		{
			// captured before the hash log became configurable
			var expected = new Dictionary<string, string> {
				["chain-16K-0"] = "5E447358F634982E2CBED692F06E6E08",
				["chain-64K-4"] = "6255EA5375B086E436C6F181B50D2290",
				["chain-dict-16K-4"] = "089E38A86539F5516DA6D144D4E2BF70",
				["block-32K"] = "99DB06842E804DCCA356A5E88001981F",
				["block-300K"] = "58F9E3BE3B51A4FE9446C90ACDBEF940",
			};
			Assert.Equal(expected, HashLogTestKit.DefaultOutputHashes());
		}

		[Fact]
		public void ExplicitDefaultHashLogShouldProduceSameOutput()
		{
			var data = HashLogTestKit.Corpus(7, 200_000);
			var dictionaryBytes = HashLogTestKit.Corpus(8, 64 * 1024);
			using var dictionary = new LZ4FastDictionary(dictionaryBytes);
			using var explicitDictionary = new LZ4FastDictionary(dictionaryBytes, 12);
			using var implicitEncoder = new LZ4FastChainEncoder(BlockSize, 4, dictionary);
			using var explicitEncoder = new LZ4FastChainEncoder(BlockSize, 4, explicitDictionary, 12);

			Assert.Equal(12, implicitEncoder.HashLog);
			Assert.Equal(12, dictionary.HashLog);
			Assert.Equal(
				HashLogTestKit.Encode(implicitEncoder, data, 3000),
				HashLogTestKit.Encode(explicitEncoder, data, 3000));
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void BlocksEncodedWithHashLog16ShouldDecodeWithRegularDecoders(bool withDictionary)
		{
			var data = HashLogTestKit.Corpus(9, 500_000);
			var dictionaryBytes = withDictionary
				? HashLogTestKit.Corpus(10, 64 * 1024)
				: Array.Empty<byte>();
			using var dictionary = withDictionary ? new LZ4FastDictionary(dictionaryBytes, 16) : null;
			using var encoder = dictionary is null
				? new LZ4FastChainEncoder(BlockSize, 0, 16)
				: new LZ4FastChainEncoder(BlockSize, 0, dictionary, 16);
			var blocks = HashLogTestKit.Encode(encoder, data, 5000);

			using var decoder = new LZ4ChainDecoder(BlockSize, 0, dictionary);
			Assert.Equal(data, HashLogTestKit.Decode(decoder, blocks));

			// every block decoded on its own, with all preceding bytes as the dictionary
			var history = new List<byte>(dictionaryBytes);
			var target = new byte[BlockSize];
			foreach (var block in blocks)
			{
				var window = history.Skip(Math.Max(0, history.Count - 64 * 1024)).ToArray();
				var decoded = LZ4Codec.Decode(block, target, window);
				Assert.True(decoded > 0);
				history.AddRange(target.Take(decoded));
			}
			Assert.Equal(data, history.Skip(dictionaryBytes.Length).ToArray());
		}

		[Fact]
		public void SingleBlockEncodedWithHashLog16ShouldDecodeWithLZ4Codec()
		{
			var data = HashLogTestKit.Corpus(11, 256 * 1024);
			using var encoder = new LZ4FastChainEncoder(data.Length, 0, 16);
			var blocks = HashLogTestKit.Encode(encoder, data, data.Length);
			Assert.Single(blocks);

			var target = new byte[data.Length];
			Assert.Equal(data.Length, LZ4Codec.Decode(blocks[0], target));
			Assert.Equal(data, target);
		}

		[Fact]
		public void LargerHashLogShouldCompressNoWorse()
		{
			var data = HashLogTestKit.Corpus(12, 1_000_000);
			int Size(int hashLog)
			{
				using var encoder = new LZ4FastChainEncoder(64 * 1024, 0, hashLog);
				return HashLogTestKit.Encode(encoder, data, 64 * 1024).Sum(b => b.Length);
			}

			var size12 = Size(12);
			var size16 = Size(16);
			_output.WriteLine($"hashLog 12: {size12}, hashLog 16: {size16}");
			Assert.True(size16 <= size12, $"hashLog 12: {size12}, hashLog 16: {size16}");
		}

		[Theory]
		[InlineData(9)]
		[InlineData(17)]
		[InlineData(-1)]
		public void InvalidHashLogShouldThrow(int hashLog)
		{
			Assert.Throws<ArgumentOutOfRangeException>(
				() => new LZ4FastChainEncoder(BlockSize, 0, hashLog));
			Assert.Throws<ArgumentOutOfRangeException>(
				() => new LZ4FastDictionary(new byte[100], hashLog));
		}

		[Fact]
		public void DictionaryWithDifferentHashLogShouldBeRejected()
		{
			var bytes = HashLogTestKit.Corpus(13, 16 * 1024);
			using var dictionary14 = new LZ4FastDictionary(bytes, 14);
			using var dictionary16 = new LZ4FastDictionary(bytes, 16);

			Assert.Throws<ArgumentException>(
				() => new LZ4FastChainEncoder(BlockSize, 0, dictionary16));
			Assert.Throws<ArgumentException>(
				() => new LZ4FastChainEncoder(BlockSize, 0, dictionary16, 14));

			using var encoder = new LZ4FastChainEncoder(BlockSize, 0, dictionary14, 14);
			Assert.Throws<ArgumentException>(() => encoder.Reset(dictionary16));
			Assert.Same(dictionary14, encoder.Dictionary);

			encoder.Reset(null);
			encoder.Reset(dictionary14);
			var data = HashLogTestKit.Corpus(14, 50_000);
			using var decoder = new LZ4ChainDecoder(BlockSize, 0, dictionary14);
			Assert.Equal(data, HashLogTestKit.Decode(decoder, HashLogTestKit.Encode(encoder, data, 1000)));
		}
	}

	[CollectionDefinition(Name, DisableParallelization = true)]
	public class Enforce32Collection
	{
		public const string Name = "Enforce32";
	}

	// Enforce32 is a process-wide switch, so these tests can't run next to the others
	[Collection(Enforce32Collection.Name)]
	public class LZ4HashLog32Tests
	{
		[Fact]
		public void DefaultOutputShouldNotChange()
		{
			// captured before the hash log became configurable
			var expected = new Dictionary<string, string> {
				["chain-16K-0"] = "CA7DFEA4BD66133F572D500F8CAA03B7",
				["chain-64K-4"] = "88283C428019D619E89D013244A9FA9D",
				["chain-dict-16K-4"] = "57B219146E2863385505D423749FADAC",
				["block-32K"] = "99DB06842E804DCCA356A5E88001981F",
				["block-300K"] = "F544D336462EBB3CEC6961C48D0CECC9",
			};
			Assert.Equal(expected, With32(HashLogTestKit.DefaultOutputHashes));
		}

		[Theory]
		[InlineData(10, 0, true)]
		[InlineData(16, 0, false)]
		[InlineData(16, 4, true)]
		public void Roundtrip(int hashLog, int extraBlocks, bool withDictionary)
		{
			With32(() => {
				HashLogTestKit.AssertRoundtrip(hashLog, extraBlocks, withDictionary);
				return 0;
			});
		}

		private static T With32<T>(Func<T> action)
		{
			LZ4Codec.Enforce32 = true;
			try
			{
				return action();
			}
			finally
			{
				LZ4Codec.Enforce32 = false;
			}
		}
	}

	internal static class HashLogTestKit
	{
		public static void AssertRoundtrip(int hashLog, int extraBlocks, bool withDictionary)
		{
			const int blockSize = 16 * 1024;
			using var dictionary = withDictionary
				? new LZ4FastDictionary(Corpus(20, 64 * 1024), hashLog)
				: null;
			var data = Corpus(21, 400_000);

			using var encoder = dictionary is null
				? new LZ4FastChainEncoder(blockSize, extraBlocks, hashLog)
				: new LZ4FastChainEncoder(blockSize, extraBlocks, dictionary, hashLog);
			using var decoder = new LZ4ChainDecoder(blockSize, extraBlocks, dictionary);
			Assert.Equal(hashLog, encoder.HashLog);
			Assert.Equal(data, Decode(decoder, Encode(encoder, data, 3000)));

			encoder.Reset();
			decoder.Reset();
			var more = Corpus(22, 100_000);
			Assert.Equal(more, Decode(decoder, Encode(encoder, more, 7000)));
		}

		public static Dictionary<string, string> DefaultOutputHashes()
		{
			var result = new Dictionary<string, string>();

			using (var encoder = new LZ4FastChainEncoder(16 * 1024, 0))
				result["chain-16K-0"] = Hash(Encode(encoder, Corpus(1, 300_000), 5000));

			using (var encoder = new LZ4FastChainEncoder(64 * 1024, 4))
				result["chain-64K-4"] = Hash(Encode(encoder, Corpus(2, 1_000_000), 100_000));

			using (var dictionary = new LZ4FastDictionary(Corpus(3, 64 * 1024)))
			using (var encoder = new LZ4FastChainEncoder(16 * 1024, 4, dictionary))
				result["chain-dict-16K-4"] = Hash(Encode(encoder, Corpus(4, 300_000), 3000));

			result["block-32K"] = Hash(new[] { EncodeBlock(Corpus(5, 32 * 1024)) });
			result["block-300K"] = Hash(new[] { EncodeBlock(Corpus(6, 300_000)) });

			return result;
		}

		// Text built from a few thousand random words, frequent ones much more often than rare ones,
		// so there are many repetitions at all distances
		public static byte[] Corpus(int seed, int length)
		{
			var random = new Random(seed);
			var words = Enumerable.Range(0, 4096).Select(_ => Word(random)).ToArray();
			var text = new StringBuilder(length + 64);
			while (text.Length < length)
			{
				text.Append(words[random.Next(random.Next(1, words.Length))]);
				if (random.Next(16) == 0)
					text.Append(", ").Append(random.Next(10000)).Append('\n');
				else
					text.Append(' ');
			}
			return Encoding.ASCII.GetBytes(text.ToString(0, length));
		}

		private static string Word(Random random)
		{
			var chars = new char[random.Next(3, 13)];
			for (var i = 0; i < chars.Length; i++)
				chars[i] = (char)('a' + random.Next(26));
			return new string(chars);
		}

		public static List<byte[]> Encode(LZ4FastChainEncoder encoder, byte[] data, int chunkSize)
		{
			var blocks = new List<byte[]>();
			var target = new byte[LZ4Codec.MaximumOutputSize(encoder.BlockSize)];
			for (var offset = 0; offset < data.Length; offset += chunkSize)
			{
				var source = data.AsSpan(offset, Math.Min(chunkSize, data.Length - offset));
				while (source.Length > 0)
				{
					encoder.TopupAndEncode(
						source, target, true, false, out var loaded, out var encoded);
					Assert.True(loaded > 0 && encoded > 0);
					blocks.Add(target.AsSpan(0, encoded).ToArray());
					source = source.Slice(loaded);
				}
			}
			return blocks;
		}

		public static byte[] Decode(LZ4ChainDecoder decoder, List<byte[]> blocks)
		{
			var output = new List<byte>();
			var target = new byte[decoder.BlockSize];
			foreach (var block in blocks)
			{
				if (!decoder.DecodeAndDrain(block, target, out var decoded))
					throw new InvalidOperationException("Failed to decode block");
				output.AddRange(target.Take(decoded));
			}
			return output.ToArray();
		}

		public static byte[] EncodeBlock(byte[] data)
		{
			var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
			var encoded = LZ4Codec.Encode(data, target);
			return target.AsSpan(0, encoded).ToArray();
		}

		public static string Hash(IEnumerable<byte[]> blocks)
		{
			using var sha = SHA256.Create();
			var bytes = blocks.SelectMany(b => BitConverter.GetBytes(b.Length).Concat(b)).ToArray();
			return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 32);
		}
	}
}
