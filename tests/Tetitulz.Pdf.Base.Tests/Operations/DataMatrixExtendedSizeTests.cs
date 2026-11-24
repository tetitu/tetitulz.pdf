// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The Data Matrix sizes past 52×52 and the DMRE sizes, each from an independent writer (checked in as
/// module rows): ISO 21471's rectangular extension (DMRE) — 20×44, and 26×40 and 26×48, the two whose
/// placement needs Annex E's row wrap — and ISO/IEC 16022's larger squares: 64×64 (four regions a
/// side, two blocks), 104×104 (six blocks) and 144×144 (ten uneven blocks, its error correction in the
/// order an independent reader writes).
/// </summary>
public class DataMatrixExtendedSizeTests
{
    private const string Text = "THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG 0123456789 ";

    [Theory]
    [InlineData("Dmre20x44", 44, 74)]
    [InlineData("Dmre26x40", 40, 95)]
    [InlineData("Dmre26x48", 48, 128)]
    [InlineData("Square64", 64, 305)]
    [InlineData("Square104", 104, 1046)]
    [InlineData("Square144", 144, 1955)]
    public void ASymbolOfThisSize_IsRead(string fixture, int width, int length)
    {
        string expected = string.Concat(Enumerable.Repeat(Text, 60)).Substring(0, length);
        Assert.Equal(expected, DataMatrixGridReader.Decode(Grid(Fixture(fixture), width), out string refusal, out _));
        Assert.Null(refusal);
    }

    /// <summary>
    /// 144×144's two error-correction orders: ISO/IEC 16022's puts block b's codewords at b, b + 10, …;
    /// the one an independent reader reads and an independent writer writes by default shifts every block's by two (an independent writer's dm_ecc skew,
    /// an independent reader's DataBlock version 24). The first block of data is 156 codewords, the last two 155.
    /// </summary>
    [Fact]
    public void TheLargestSymbol_HasBothOrders_AndUnevenBlocks()
    {
        int symbol = DataMatrixLayout.WithSize(144, 144);
        int data = DataMatrixLayout.DataCodewords(symbol), ec = DataMatrixLayout.EcCodewords(symbol);
        var stream = Enumerable.Range(0, data + ec).ToArray();

        int[] first = DataMatrixLayout.Block(symbol, stream, 0);
        int[] last = DataMatrixLayout.Block(symbol, stream, 9);
        Assert.Equal(156 + 62, first.Length);
        Assert.Equal(155 + 62, last.Length);
        Assert.Equal(data, first[156]);                    // ISO: block 0's error correction starts at its own index
        Assert.Equal(data + 2, DataMatrixLayout.Block(symbol, stream, 0, skewed: true)[156]);
        Assert.Equal(data + 1, DataMatrixLayout.Block(symbol, stream, 9, skewed: true)[155]);   // (9 + 2) mod 10
    }

    /// <summary>
    /// A 144×144 symbol in the other order is read too: the independent writer writes ISO's, so this one is made
    /// from it — every block's error correction moved two places on and written back through the
    /// placement walk the reader reads by — and read through the retry.
    /// </summary>
    [Fact]
    public void ALargestSymbolInTheSkewedOrder_IsRead()
    {
        bool[,] grid = Grid(Square144, 144);
        int symbol = DataMatrixLayout.WithSize(144, 144);
        int data = DataMatrixLayout.DataCodewords(symbol), ec = DataMatrixLayout.EcCodewords(symbol);
        int[] iso = DataMatrixGridReader.ReadCodewords(symbol, grid);

        var skewed = (int[])iso.Clone();
        for (int k = 0; k < ec / 10; k++)
            for (int b = 0; b < 10; b++)
                skewed[data + k * 10 + (b + 2) % 10] = iso[data + k * 10 + b];

        int mapRows = DataMatrixLayout.MappingRows(symbol), mapColumns = DataMatrixLayout.MappingColumns(symbol);
        var positions = new (int Codeword, int Bit)?[mapRows, mapColumns];
        Ecc200Placement.Trace(positions, mapRows, mapColumns);
        for (int y = 0; y < mapRows; y++)
            for (int x = 0; x < mapColumns; x++)
            {
                if (!(positions[y, x] is (int codeword, int bit)) || codeword >= skewed.Length) continue;
                DataMatrixLayout.ToSymbol(symbol, y, x, out int row, out int column);
                grid[row, column] = (skewed[codeword] & (1 << (8 - bit))) != 0;
            }

        Assert.Equal(skewed, DataMatrixGridReader.ReadCodewords(symbol, grid));
        Assert.Equal(string.Concat(Enumerable.Repeat(Text, 60)).Substring(0, 1955), DataMatrixGridReader.Decode(grid, out string refusal, out _));
        Assert.Null(refusal);
    }

    /// <summary>The encoder never writes a DMRE size or 144×144, whose order readers disagree on.</summary>
    [Fact]
    public void TheEncoder_WritesNeitherExtensionsNorTheLargestSymbol()
    {
        for (int codewords = 1; codewords <= 1304; codewords += 7)
        {
            foreach (bool rectangular in new[] { false, true })
            {
                int symbol = DataMatrixLayout.SmallestFor(codewords, rectangular);
                if (symbol < 0) continue;
                Assert.False(DataMatrixLayout.IsExtension(symbol));
                Assert.False(DataMatrixLayout.IsLargest(symbol));
            }
        }
        Assert.Equal(132, DataMatrixLayout.Rows(DataMatrixLayout.SmallestFor(1304)));
        Assert.Equal(-1, DataMatrixLayout.SmallestFor(1305));
    }

    private static string[] Fixture(string name) => name switch
    {
        "Dmre20x44" => Dmre20x44,
        "Dmre26x40" => Dmre26x40,
        "Dmre26x48" => Dmre26x48,
        "Square64" => Square64,
        "Square104" => Square104,
        "Square144" => Square144,
        _ => throw new ArgumentException(name),
    };

    /// <summary>A grid from hex rows: each row's last <paramref name="width"/> bits, the first the leftmost module; true is dark.</summary>
    private static bool[,] Grid(string[] rows, int width)
    {
        var grid = new bool[rows.Length, width];
        for (int y = 0; y < rows.Length; y++)
        {
            string bits = string.Concat(rows[y].Select(h => Convert.ToString(Convert.ToInt32(h.ToString(), 16), 2).PadLeft(4, '0')));
            bits = bits.Substring(bits.Length - width);
            for (int x = 0; x < width; x++) grid[y, x] = bits[x] == '1';
        }
        return grid;
    }

    // ---------------------------------------------------------------- an independent reader's symbols

    private static readonly string[] Dmre20x44 =
    {
        "aaaaaaaaaaa",
        "ed7d37fcf0f",
        "abac7ad16ec",
        "945756d4157",
        "a6a02b6f754",
        "8d6966dec97",
        "a7f2a32cc68",
        "f435fe873e1",
        "ba1f424b094",
        "913d362c1e9",
        "febc4bf7c1a",
        "8659fe70fc1",
        "aa9d1ab0b2a",
        "f297ffc6e0d",
        "86c863864e8",
        "d960771dcf5",
        "fe4b023007a",
        "d8712f24ffd",
        "b34cfbbd8c6",
        "fffffffffff",
    };

    private static readonly string[] Dmre26x40 =
    {
        "aaaaaaaaaa",
        "ed7d7e84df",
        "abac6e89b2",
        "9456fb9de5",
        "a6a1ed5e4e",
        "8d6b5d1537",
        "a7f7c922ac",
        "f4359bd867",
        "ba01cf1fa4",
        "91151b550f",
        "feacaacf30",
        "8756fb62a5",
        "abe42fb580",
        "f3c3bfb6a9",
        "83378b34ea",
        "c543de0da9",
        "eb9f8bf282",
        "a873bb2f95",
        "ec4b29847c",
        "9951fa740d",
        "dfa32c8bc6",
        "a34d9e0ced",
        "fc374cd6a2",
        "929bbd426f",
        "8cb3899856",
        "ffffffffff",
    };

    private static readonly string[] Dmre26x48 =
    {
        "aaaaaaaaaaaa",
        "ed7d03d3114f",
        "abac44f944be",
        "94568db1d391",
        "a6a1d6892828",
        "8d6b45c03d39",
        "a7f7b6cca9b4",
        "f435a395fbbf",
        "ba00d6a322d4",
        "91154dfb4ff7",
        "feafaacbb000",
        "875fd7e50aa9",
        "abdc70ceb890",
        "f3f529efa3f1",
        "830e34ac7b1e",
        "c54c39c73de1",
        "ebe64e8c61b2",
        "a9d36d9e00ad",
        "e05100e658a4",
        "909951820455",
        "e2396cfa5126",
        "977dc7ff2749",
        "b0b3a2c06ca4",
        "b61907a561ff",
        "8c737cb9fb2e",
        "ffffffffffff",
    };

    private static readonly string[] Square64 =
    {
        "aaaaaaaaaaaaaaaa",
        "ed7dc0cf9315d033",
        "abac916ea68eda88",
        "9457a369ed6ffddf",
        "a6a0f58ac06ceb0c",
        "8d6bd1279117fb47",
        "a7f6ec70ab22a8a4",
        "f435ece9fca9e7a1",
        "ba00b634ff80c868",
        "9115cc31fe79ed87",
        "feaee2528f04a8cc",
        "875fd33fc6d9fa71",
        "abdcf176896ca0ea",
        "f3f1a83bdf6dd32d",
        "830cc8ace58eb570",
        "ffffffffffffffff",
        "aaaaaaaaaaaaaaaa",
        "c553dbb781559221",
        "ebeac82aed7aad26",
        "a9f5ae55ede3ef9d",
        "e012fd68b3fee130",
        "914b8e75d15f914f",
        "e38af31ee3108316",
        "930fc90fedbf90e7",
        "b154ad5cf82aee42",
        "b415e7b1eb1f8625",
        "95188b0cbddaef70",
        "a437f8558891b5ff",
        "86ccabfa84e4a1e8",
        "d5a7e189d4abaab7",
        "b218c01ea656d048",
        "ffffffffffffffff",
        "aaaaaaaaaaaaaaaa",
        "9b0ff1a7f461f95f",
        "942aa36ae126d83a",
        "9f25d5b3d613b7bd",
        "d38ab3c2e3e28f2e",
        "fd55b0ffece5ac3b",
        "88fc8ec4fce8ce04",
        "8741980184b3ec61",
        "879ca0eec414d7e2",
        "9105c127b85d9d35",
        "bfa8a4d8e9a2a542",
        "c0af98f7e3e5f5a5",
        "e92c801ae0f68a78",
        "94ebad2fc4c7e83b",
        "ba0ef666abacc5e0",
        "ffffffffffffffff",
        "aaaaaaaaaaaaaaaa",
        "9d21f645fe7bd775",
        "f85aa0ee964ef268",
        "8cdbee27fa95874d",
        "a6e0b0f8dbe6bc5a",
        "ccd18d2588a5e4a5",
        "a254f09ae9b0e5be",
        "d0879dd5f803fe8f",
        "f12eb41eec5cbd3e",
        "a8bfb5abbe738aa1",
        "e3e6a1408fb0a4c4",
        "f6d7f6e3a353ca2b",
        "fc1884ea915ec11c",
        "b4b9a805f6af9d5d",
        "cc52ff1088f8aaaa",
        "ffffffffffffffff",
    };

    private static readonly string[] Square104 =
    {
        "aaaaaaaaaaaaaaaaaaaaaaaaaa",
        "ed7d036e4c551fe54316e40df5",
        "abac45ae9a3da8ff6e8ae916b0",
        "94568de9b5bf980fed7e9a315f",
        "a6a1d62b01b6a98da062b75a84",
        "8d6b44e6445f5a17914e6d15b7",
        "a7f7b1b0ac8a2922ab1b1edfd0",
        "f435b3e9f2a7bca87d3ed6d085",
        "ba00d8b5fe03eb817f8b036840",
        "911530f1f9ea9e7c7b0e5444ef",
        "feaf89323c08af048892bffb32",
        "875f4cff1b279ecfd4d7781999",
        "abddc5b62586297d9c5b72aa62",
        "f3f0a0fb7cdf7f0e8a3fc5d32d",
        "830d22ad96178dab52322c18bc",
        "c5536ef6000618ed36574534f9",
        "ebeb20abb133ac0ab2bb9fde42",
        "a9f4b975a255bb974a9f4f64f5",
        "e013f5a8d5c8cf593e029da2ac",
        "914a39f577f51b94b906f71c6f",
        "e38bcc3f999fecd8be2a0f1f66",
        "930f246e790fda30995f514207",
        "b154b53ccf5d2b152b335af37a",
        "b4159ef06fb5794173b79d51d9",
        "95182c2c0b0e0b51b0aaa9949c",
        "ffffffffffffffffffffffffff",
        "aaaaaaaaaaaaaaaaaaaaaaaaaa",
        "a437e174584dfa46007f63dce5",
        "86ccafb9ebf3286b713ac98032",
        "d5a7866861e9fd550c5edb48c7",
        "b219003640060b2aa6aa0b8268",
        "9b0fc6edf0c379f6f4275f6caf",
        "942a8d3aa30a6914b6b2a4dc3e",
        "9f25567957cc19024ecf83726d",
        "d38acfa2b4e22d69dba26ec582",
        "fd54c1753f465fb47b7722d0a5",
        "88fc38bf02238be428a2d6782c",
        "8740677001e4782184e6e5cc49",
        "879c87a721bc2a900882d73ac2",
        "910511614409993b9d37e067f5",
        "bfa8b7aa2e9f0ac05eda137616",
        "c0aec0ef93e7fa6d15d748fef5",
        "e92fe92b9efa8e973a821cddf0",
        "94eb94ea3905fcf3e3a7b3c6b9",
        "ba08ba289d894e5ff8bbaebbc6",
        "9d2d1ce59a543d0350eec8f9bd",
        "f8587820a97a4ecb15ab3f9900",
        "8ced0b757d4d7ab985776d7005",
        "a6b2a02b7ba98b082e029f341e",
        "ccc340fba10f1ceaba66623105",
        "a0a8a6202fdfeffde5a3e0df16",
        "ffffffffffffffffffffffffff",
        "aaaaaaaaaaaaaaaaaaaaaaaaaa",
        "d15e8b6ee011f8cec42ecb82df",
        "f5b9da398959cf84132b4c23f4",
        "a9bfde6f959f7c4c61cfdf3c09",
        "cc9449a5b920cba6232ba19d2c",
        "e041d0f111655ddaae361a3bd3",
        "b1a3ef2c4cffaae7d1d227a37a",
        "8cc1407516db7c71b2176c5d97",
        "a693b1b2a204093c8b43be538c",
        "f4d223e3691a5c590c4f4dccf7",
        "bdeff9aace23e9679513727556",
        "c024d97db133fad6a2c6e6f191",
        "d471cebc6b4768728b0b921de6",
        "de9d6efad3fcb89d67cf456bc9",
        "95800a2c3102e9a4aa531ae902",
        "96a5416907d738959497409b8d",
        "cfc9e63270500bc18af242bdee",
        "c0d433670065fe55667f1f8823",
        "b8761136aab70cb093122c488e",
        "e4fdd966d3dcba1dcae69f2561",
        "badac92483b70e9ff52ab58422",
        "99591bf3e40959a0c1d674be5d",
        "aae7cfa8ea3ba89b20b3ed6ee2",
        "ca5123f282b97f0870d65fd733",
        "9a30eba2ed9bacc577ba6a20f8",
        "ffffffffffffffffffffffffff",
        "aaaaaaaaaaaaaaaaaaaaaaaaaa",
        "e586b8e9fa577d332ade19d931",
        "f1c28a21c8642f580c1af8ca26",
        "e4e2e4f48ee15a173ba66ab2c9",
        "ad15ca207d6328345f8b50871a",
        "f180e869508bddccbd9f535d8f",
        "fa4799a310d66830736bcef0ca",
        "f31ce26196b4ba6658dfd4b82b",
        "b9b1563af8b38ac0de0beecc00",
        "b55475e8a32919a03d27c31563",
        "dcea18beb5892cbcd002fe71b0",
        "e6dc7be056b73d03b8167e6cb5",
        "a528ff22a958ea59a8fb16a856",
        "86884277e4d93feeb0471badf5",
        "b0dce9a9f2080f296b324fd42c",
        "cc1473f398f0dec4137fc79229",
        "9158793534aecc4f97ca226d1a",
        "fd8a10f8e1025fe4d537909cc3",
        "c914bcaa97016a53fc729443e6",
        "f1a5666439dabbbf2a1fffb80f",
        "8d6a1fb702778f6268922571b8",
        "9587f4facd4a1ff60cc78c3789",
        "a34d34a872afaf8f7fbb9a201e",
        "f07f24ec3bf95dde0ea64606f9",
        "cc33dca01bca8f1ea6fa00f1ce",
        "ffffffffffffffffffffffffff",
    };

    private static readonly string[] Square144 =
    {
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "ed7d03ce4c55c7e543c4e40dfd6d75a33045",
        "abac44ee9a3ce8ff6ea2e916ac2bbe8897aa",
        "94568de9b5bfe00fedde9a31d61445ddf9dd",
        "a6a1d68b01b6a98da098b75aa067e2b0bade",
        "8d6b45a6445fd21791d26d15eb0c5fb58cef",
        "a7f7b0f0ac8a8922aac71edef1241a8a801c",
        "f435b3e9f2a7eca87dced6d1bd6141fe13b7",
        "ba00d8b5fe02fb817ee303688c32e684fe0c",
        "911531b1f9eba67c7bc25445913933dfe347",
        "feaf88d23c08af0488a4bffaf2c99a89dbf4",
        "875f4dbf1b27e6cfd5b5781d9e63df970dcd",
        "abddc4f62586897d9c9772aca088c8a0f028",
        "f3f0a1bb7cdfdf0e8b8fc5c1f4d915d401df",
        "830d22ad9616e5ab528c2c12a60feed52254",
        "c5536fb6000780ed3795453bed51559a1501",
        "ebeb20abb132ec0ab2af9fc294b3baafb6a2",
        "a9f4b9d5a255eb974ba74e65db8613ed2cc5",
        "e013f4e8d5c8b7593e809cbae84ff2ff069c",
        "914a39f577f5c394b9c0f4d7cce405d450d1",
        "e38bcc9f999efcd8be8a0eeee930aa875dce",
        "930f258e790ff23099d74d91f99941d09055",
        "b154b4dccf5ccb152acd57f8a2836aaa1454",
        "ffffffffffffffffffffffffffffffffffff",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "b4159fb06fb5d94173edd91dd3b1f5ef5bef",
        "95182c8c0b0e8351b0aaec2ebbc5bcc8960e",
        "a437e1d4584dfa46019fc765b4d3bdb61f0f",
        "86ccaef9ebf2c86b70cfe0fad822ba84d236",
        "d5a7878861e9fd550d946ee5e4385dc12175",
        "b21900964006832aa6aef2feba80b8aa13a0",
        "9b0fc7adf0c3d9f6f587295d9dfa89e72ae1",
        "942a8cdaa30a9914b6aa0f1eab06a0adbc2a",
        "9f25579957cd81024fd931c7dbd959d8c41f",
        "d38acee2b4e28d69dabee860897caec36518",
        "fd54c1d53f4797b47fb6da75871a83b561cf",
        "88fc38bf0222e3e42aaa3cbe95a9a2c36b4e",
        "874067d001e5982189f10dbbc17ef3fb2cd9",
        "879c86e721bc8a900c828370ed95a4d8f45a",
        "910511c14409e13bb1c75bddee0673e3b67f",
        "bfa8b6ea2e9ec2c06ef7a82cb3a712c33f5e",
        "c0aec1af93e7fa6cdfc5a511d5c699a334a1",
        "e92fe8cb9efaa6961ccab57eea8b8281a992",
        "94eb95aa3905fcf133fcc71ff4df87d62b4d",
        "ba08ba889d88d65a0ec1ab1c9ddb56d8a4f2",
        "9d2d1da59a558d0743d3787580e1339e5a51",
        "f8587880a97a96d54af83c0cd426baca2cf0",
        "ffffffffffffffffffffffffffffffffffff",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "8ced0bd57d4ddac5bbe44e5f9b9f259e5b19",
        "a6b2a08b7ba8e34a86cab3ee968f58d421f0",
        "ccc341bba10fc4feed9c19e183acfb912efd",
        "a0a8a6802fdeffcfc2d6a022dbf05acb4908",
        "d15e8bcee011f8d0d59f31858c792b86466d",
        "f5b9da998958f2af549ba328fe9936fa63de",
        "a9bfdf8f959fce1c6ffb57d9867bcfd7d349",
        "cc9448e5b920a1b2bcadb2b487c00c8042aa",
        "e041d1b11165bbd78f962569a47951a04fc7",
        "b1a3eecc4cfef6e3a09b0b1eccfdb2d889cc",
        "8cc1419516d9bed4eff056b78257a3e06149",
        "a693b0f2a206cb2a00f7737ec5ad84b4e214",
        "f4d223e3691da683b5914355b28007d2acbb",
        "bdeff8eace30806f5eaba422e20dc4ca22ca",
        "c024d9ddb157ece3c7e9315fc0a78dea5843",
        "d471cebc6b6091bd68cf121ae9532ebc3404",
        "de9d6fbad2ddfff059a0954ddeb1b7f389f9",
        "95800a8c3116c2cb3a889ee092017efa1570",
        "96a541c906b7f95bbd95bd5fc32155b92cd1",
        "cfc9e6927058818cb8bcb9ecbe1082aa2c86",
        "c0d433c70c01bb524d925201d5acb9e0bae1",
        "b87610d6b2c884fba0ce88dec34ed287d4fa",
        "ffffffffffffffffffffffffffffffffffff",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "e4fdd9c6fed3daec55aa9c99ed2701f5099d",
        "badac8c4c304e1d8a8ae08f2bbbdc2c16872",
        "99591bf3f5edb3368db95bc989d94798b807",
        "aae7cee83ed6c089a4dcb69cdcd536b5ab28",
        "ca5123f03a87f44f719bace381256fa53d51",
        "9a30eae272b4be94d0a8224cd1a01ebe19ea",
        "e586b9b4c9bfd9c6c5fd589fd2110de17385",
        "f1c28a8020c085a26ea9e660bcc586852846",
        "e4e2e5f9350191c159b9bcd7cd2141aa26fb",
        "ad15cabf8fa8b2872ca54e069796fcb3f3ac",
        "f180eba60f45bcb1d5d04497e13ca3bae9c5",
        "fa479a805c2cfd992eab0ee8978350b98bb2",
        "f31ce9c0b789b1d1d5e65ba9c5c24f8c7761",
        "b9b1428d0bc294cc7af706dcd76b8abdb3ca",
        "b55435dd4a0f9914239ff5cfc2ac33ec1d2f",
        "dcea0ac7e9c297e5fc8b795c81a45c964e3e",
        "e6dc039da439911e5bd3ee4bee3637cae127",
        "a52870b00af2d30f06ab90caf9c580ad42f4",
        "86888190da35866193b9046de73b359673c7",
        "b0dd74a213e8c7a864e0561a8ba50cd57a3a",
        "cc1d15900f1be98b6189ef1387031b83624d",
        "9146bab1a4b4bf3cf4d8b8e4b75060dc7c66",
        "ffffffffffffffffffffffffffffffffffff",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "fd9e89a2d56787c9ddca3be7ac0a61ef753d",
        "c9349c979b08e281429ee2da886256e22530",
        "f013a1f4b6cf8549bdc25905ed4a35bb4969",
        "8d295c9a4368cbb8fef10c42e8a1fac01dba",
        "953581f5a2ddd5e435e136f5c3c81dd041af",
        "a6e3aaca257afabbc680969eadda36ba882a",
        "e230a1eb35a5cca7fdffbf67b87a6be8349f",
        "c68d329c162eb00a869e70cefd3844cb03d0",
        "e918d3ff9ce5bd30099ba6b1d9377fa55791",
        "d2aa64d09d9eb5e79aaf1b9cbe1e7a8d321a",
        "eb4c21fdc781853289cd1de99f27cdc9650d",
        "e638688ce5aca9503ee72e42d04602a6e496",
        "bb8eb1827055be0fe5bd684fb8421199a2af",
        "d3a5a28e8cd8fc9c40ebcecade1f7093a1fa",
        "9a5635ad946ffcd2ab8ada19a4e34dd35575",
        "80af60ab711cef9c62e6c47ab32c7ce1536a",
        "cac079bbf4098f6505bc2a9bf2ec13ffb951",
        "99b534ca6a68bbeef2df4a30cfdeaa8b1fbc",
        "be66a1a60dcb8ee561f1e2739427e3d4a079",
        "94a490b3a0fac66e7cf1c056c5e7428441ba",
        "850243cb7c3fc15e81d31121879cddfc9acf",
        "a0306e82f118e196ba941deae93898cc193e",
        "ffffffffffffffffffffffffffffffffffff",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "ce4d4dd6c5519b8adfc17b99c8dfcb973add",
        "8e8320fa71daf58d40c8d524fdd326da27ce",
        "e98191817eb3c2072ff38d07e3ef9fb5d80d",
        "8b192687ba20c38a9cc28202d4cb38f79722",
        "a6207db33871dad869c4931d9ec3f7be88cb",
        "f14d5a99c05ee28e5cf9c5aad533c0d5679a",
        "ef545fd386cb8da34bc23013e33095f9a40d",
        "b1e4e2c660aad7b020adcb089fd590be317a",
        "a70f45a06d85da32a7f2aee5d9e46dbdbd8d",
        "cf1e3e96772abafab0aa2d7c87be1883e7c8",
        "860dc7f3077faabb3fbffceda95f1b84f263",
        "c7a1c48fb69e9b27ccacdb08ba5c1ea07e10",
        "c8e41391c7398c5a8bd65d63e7fad79bad9d",
        "be2d3e85a55af2ea14ecce6ea100eef43488",
        "83b3a3eda38bc30c8fd7cc0d9ce6598d9d6d",
        "e7fc6cc5f2b09f51d69092fcc53a94d13efa",
        "83e6ff8a986fd15381ee92f3cdcfbd99fedd",
        "de288a947f4aaa385e8e13b0a0c09ab0cfd6",
        "f34d3f9cd329bc252df7bce3a70efff6a9db",
        "c66a0084d158d716c2a59aa08ec42ab41e08",
        "db5b9d94d23fe43b47ba5c79d38551a357eb",
        "dcc53ef3003cea914a83c816c732ca91aa98",
        "ffffffffffffffffffffffffffffffffffff",
    };
}