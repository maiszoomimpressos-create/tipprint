using System;
using System.Collections.Generic;

// Gerador de QR Code puro C# (sem dependencias externas - o build compila direto com csc.exe,
// sem NuGet, entao nao da pra usar uma lib pronta).
//
// Por que isso existe: o comando ESC/POS nativo de QR Code (GS ( k, usado antes em WriteQr)
// depende do firmware da impressora interpretar certo um comando de varios bytes: se a
// impressora Bluetooth perder um ou poucos bytes no meio da transmissao (RF ruim, firmware
// clone bugado), ela cai fora do "modo comando" e imprime o resto dos bytes como texto puro -
// e como o comando de "gravar dados" comeca com os bytes ASCII 'P' '0' (0x50 0x30) antes dos
// dados em si, o que vaza no cupom e' literalmente "P0<hash-do-ingresso>" legivel (achado real
// em producao, cupons de teste em 2026-08-17, mesmo ja com toda a mitigacao de chunking/pausa
// aplicada em cima do comando nativo - ver SendAndChunk). Isso e' perda de bytes no proprio
// link de radio, nao um bug de timing do nosso software, entao pausa/chunking nao resolve.
//
// A solucao aqui e' trocar de tecnica: em vez de mandar o QR como comando nativo (que exige a
// impressora decodificar os dados certinho), a gente MESMO gera a matriz do QR Code e manda
// como IMAGEM RASTER (GS v 0, o mesmo comando usado pra logos/bitmaps) - a impressora so'
// precisa carimbar pontos pretos/brancos, sem interpretar nenhum comando de dados variavel no
// meio do caminho. Se algum byte se perder, o pior caso vira uma falha visual (um pedaco do QR
// borrado) em vez de um hash inteiro vazando como texto legivel no cupom do cliente.
//
// Implementacao port dos algoritmos padrao da ISO/IEC 18004, verificada por round-trip (gerar
// aqui em C#, decodificar com leitor de QR independente) durante o desenvolvimento - ver
// docs/QR-ENCODER-NOTES.md.
internal static class QrEncoder
{
    // ---- Galois Field GF(256), usado no Reed-Solomon (correcao de erro) ----
    private static readonly byte[] ExpTable = new byte[512];
    private static readonly byte[] LogTable = new byte[256];

    static QrEncoder()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            ExpTable[i] = (byte)x;
            LogTable[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) ExpTable[i] = ExpTable[i - 255];
    }

    private static int GfLog(int n) { return LogTable[n]; }
    private static int GfExp(int n) { return ExpTable[n]; }
    private static int GfMul(int x, int y)
    {
        if (x == 0 || y == 0) return 0;
        return ExpTable[LogTable[x] + LogTable[y]];
    }

    // ---- Polinomios sobre GF(256) ----
    private static byte[] PolyMul(byte[] p1, byte[] p2)
    {
        byte[] coeff = new byte[p1.Length + p2.Length - 1];
        for (int i = 0; i < p1.Length; i++)
            for (int j = 0; j < p2.Length; j++)
                coeff[i + j] ^= (byte)GfMul(p1[i], p2[j]);
        return coeff;
    }

    private static byte[] PolyMod(byte[] dividend, byte[] divisor)
    {
        byte[] result = (byte[])dividend.Clone();
        while (result.Length - divisor.Length >= 0)
        {
            int coeff = result[0];
            for (int i = 0; i < divisor.Length; i++)
                result[i] ^= (byte)GfMul(divisor[i], coeff);
            int offset = 0;
            while (offset < result.Length && result[offset] == 0) offset++;
            byte[] shrunk = new byte[result.Length - offset];
            Array.Copy(result, offset, shrunk, 0, shrunk.Length);
            result = shrunk;
        }
        return result;
    }

    private static byte[] GenerateEcPolynomial(int degree)
    {
        byte[] poly = new byte[] { 1 };
        for (int i = 0; i < degree; i++)
            poly = PolyMul(poly, new byte[] { 1, (byte)GfExp(i) });
        return poly;
    }

    private static byte[] ReedSolomonEncode(byte[] data, int degree)
    {
        byte[] genPoly = GenerateEcPolynomial(degree);
        byte[] padded = new byte[data.Length + degree];
        Array.Copy(data, padded, data.Length);
        byte[] remainder = PolyMod(padded, genPoly);
        if (remainder.Length == degree) return remainder;
        byte[] outp = new byte[degree];
        Array.Copy(remainder, 0, outp, degree - remainder.Length, remainder.Length);
        return outp;
    }

    // ---- Buffer de bits ----
    private class BitBuf
    {
        public List<byte> Buffer = new List<byte>();
        public int Length = 0;

        public void PutBit(bool bit)
        {
            int bufIndex = Length / 8;
            if (Buffer.Count <= bufIndex) Buffer.Add(0);
            if (bit) Buffer[bufIndex] |= (byte)(0x80 >> (Length % 8));
            Length++;
        }

        public void Put(int num, int length)
        {
            for (int i = 0; i < length; i++)
                PutBit(((num >> (length - i - 1)) & 1) == 1);
        }
    }

    // ---- Matriz de modulos ----
    private class BitMatrix
    {
        public int Size;
        public bool[] Data;
        public bool[] Reserved;

        public BitMatrix(int size)
        {
            Size = size;
            Data = new bool[size * size];
            Reserved = new bool[size * size];
        }

        public void Set(int row, int col, bool value, bool reserved)
        {
            int idx = row * Size + col;
            Data[idx] = value;
            if (reserved) Reserved[idx] = true;
        }

        public bool Get(int row, int col) { return Data[row * Size + col]; }
        public void Xor(int row, int col, bool value) { Data[row * Size + col] ^= value; }
        public bool IsReserved(int row, int col) { return Reserved[row * Size + col]; }
    }

    // ---- Tabelas oficiais (ISO/IEC 18004) - portadas 1:1 da lib "qrcode" (MIT, node-qrcode) ----
    // Total de codewords (dados + correcao de erro) por versao (indice 0 nao usado).
    private static readonly int[] CodewordsCount = {
        0,
        26, 44, 70, 100, 134, 172, 196, 242, 292, 346,
        404, 466, 532, 581, 655, 733, 815, 901, 991, 1085,
        1156, 1258, 1364, 1474, 1588, 1706, 1828, 1921, 2051, 2185,
        2323, 2465, 2611, 2761, 2876, 3034, 3196, 3362, 3532, 3706
    };

    // Colunas: L, M, Q, H. Linha = versao-1.
    private static readonly int[,] EcBlocksTable = {
        {1,1,1,1},{1,1,1,1},{1,1,2,2},{1,2,2,4},{1,2,4,4},
        {2,4,4,4},{2,4,6,5},{2,4,6,6},{2,5,8,8},{4,5,8,8},
        {4,5,8,11},{4,8,10,11},{4,9,12,16},{4,9,16,16},{6,10,12,18},
        {6,10,17,16},{6,11,16,19},{6,13,18,21},{7,14,21,25},{8,16,20,25},
        {8,17,23,25},{9,17,23,34},{9,18,25,30},{10,20,27,32},{12,21,29,35},
        {12,23,34,37},{12,25,34,40},{13,26,35,42},{14,28,38,45},{15,29,40,48},
        {16,31,43,51},{17,33,45,54},{18,35,48,57},{19,37,51,60},{19,38,53,63},
        {20,40,56,66},{21,43,59,70},{22,45,62,74},{24,47,65,77},{25,49,68,81}
    };

    // Colunas: L, M, Q, H. Linha = versao-1. Total de codewords de correcao de erro (simbolo inteiro).
    private static readonly int[,] EcCodewordsTable = {
        {7,10,13,17},{10,16,22,28},{15,26,36,44},{20,36,52,64},{26,48,72,88},
        {36,64,96,112},{40,72,108,130},{48,88,132,156},{60,110,160,192},{72,130,192,224},
        {80,150,224,264},{96,176,260,308},{104,198,288,352},{120,216,320,384},{132,240,360,432},
        {144,280,408,480},{168,308,448,532},{180,338,504,588},{196,364,546,650},{224,416,600,700},
        {224,442,644,750},{252,476,690,816},{270,504,750,900},{300,560,810,960},{312,588,870,1050},
        {336,644,952,1110},{360,700,1020,1200},{390,728,1050,1260},{420,784,1140,1350},{450,812,1200,1440},
        {480,868,1290,1530},{510,924,1350,1620},{540,980,1440,1710},{570,1036,1530,1800},{570,1064,1590,1890},
        {600,1120,1680,1980},{630,1204,1770,2100},{660,1260,1860,2220},{720,1316,1950,2310},{750,1372,2040,2430}
    };

    // Nivel de correcao de erro: indice na tabela acima (L=0,M=1,Q=2,H=3) e bit code usado no
    // formato info (L=1,M=0,Q=3,H=2 - conforme spec, nao e' o mesmo valor do indice da tabela!).
    private static int EccIndex(char level)
    {
        switch (level) { case 'L': return 0; case 'M': return 1; case 'Q': return 2; case 'H': return 3; }
        throw new Exception("Nivel de correcao de erro invalido: " + level);
    }
    private static int EccFormatBit(char level)
    {
        switch (level) { case 'L': return 1; case 'M': return 0; case 'Q': return 3; case 'H': return 2; }
        throw new Exception("Nivel de correcao de erro invalido: " + level);
    }

    private static int GetSymbolSize(int version) { return version * 4 + 17; }
    private static int GetBlocksCount(int version, char level) { return EcBlocksTable[version - 1, EccIndex(level)]; }
    private static int GetTotalEcCodewords(int version, char level) { return EcCodewordsTable[version - 1, EccIndex(level)]; }

    private static int GetCharCountBits(int version)
    {
        // Modo Byte: 8 bits ate versao 9, 16 bits da versao 10 em diante.
        return version < 10 ? 8 : 16;
    }

    private static int GetBchDigit(int data)
    {
        int digit = 0;
        while (data != 0) { digit++; data >>= 1; }
        return digit;
    }

    // ---- Escolha automatica da menor versao que comporta os dados no nivel de correcao pedido ----
    private static int FindVersion(int dataLength, char level, int maxVersion)
    {
        for (int v = 1; v <= maxVersion; v++)
        {
            int totalCodewords = CodewordsCount[v];
            int ecTotal = GetTotalEcCodewords(v, level);
            int dataTotalBits = (totalCodewords - ecTotal) * 8;
            int reservedBits = 4 + GetCharCountBits(v); // indicador de modo (4 bits, Byte) + contador de caracteres
            int usableBits = dataTotalBits - reservedBits;
            int capacityBytes = usableBits / 8;
            if (dataLength <= capacityBytes) return v;
        }
        return 0;
    }

    // ---- Monta os codewords finais (dados + terminador/padding + correcao de erro intercalada) ----
    private static byte[] CreateData(int version, char level, byte[] data)
    {
        BitBuf buf = new BitBuf();
        buf.Put(4, 4); // indicador de modo Byte = 0b0100
        buf.Put(data.Length, GetCharCountBits(version));
        foreach (byte b in data) buf.Put(b, 8);

        int totalCodewords = CodewordsCount[version];
        int ecTotalCodewords = GetTotalEcCodewords(version, level);
        int dataTotalBits = (totalCodewords - ecTotalCodewords) * 8;

        if (buf.Length + 4 <= dataTotalBits) buf.Put(0, 4);
        while (buf.Length % 8 != 0) buf.PutBit(false);

        int remainingBytes = (dataTotalBits - buf.Length) / 8;
        for (int i = 0; i < remainingBytes; i++) buf.Put(i % 2 != 0 ? 0x11 : 0xEC, 8);

        return CreateCodewords(buf, version, level);
    }

    private static byte[] CreateCodewords(BitBuf bitBuffer, int version, char level)
    {
        int totalCodewords = CodewordsCount[version];
        int ecTotalCodewords = GetTotalEcCodewords(version, level);
        int dataTotalCodewords = totalCodewords - ecTotalCodewords;
        int ecTotalBlocks = GetBlocksCount(version, level);

        int blocksInGroup2 = totalCodewords % ecTotalBlocks;
        int blocksInGroup1 = ecTotalBlocks - blocksInGroup2;
        int totalCodewordsInGroup1 = totalCodewords / ecTotalBlocks;
        int dataCodewordsInGroup1 = dataTotalCodewords / ecTotalBlocks;
        int dataCodewordsInGroup2 = dataCodewordsInGroup1 + 1;
        int ecCount = totalCodewordsInGroup1 - dataCodewordsInGroup1;

        byte[] buffer = bitBuffer.Buffer.ToArray();
        byte[][] dcData = new byte[ecTotalBlocks][];
        byte[][] ecData = new byte[ecTotalBlocks][];
        int offset = 0;
        int maxDataSize = 0;

        for (int b = 0; b < ecTotalBlocks; b++)
        {
            int dataSize = b < blocksInGroup1 ? dataCodewordsInGroup1 : dataCodewordsInGroup2;
            byte[] block = new byte[dataSize];
            Array.Copy(buffer, offset, block, 0, dataSize);
            dcData[b] = block;
            ecData[b] = ReedSolomonEncode(block, ecCount);
            offset += dataSize;
            maxDataSize = Math.Max(maxDataSize, dataSize);
        }

        byte[] data = new byte[totalCodewords];
        int index = 0;
        for (int i = 0; i < maxDataSize; i++)
            for (int r = 0; r < ecTotalBlocks; r++)
                if (i < dcData[r].Length) data[index++] = dcData[r][i];
        for (int i = 0; i < ecCount; i++)
            for (int r = 0; r < ecTotalBlocks; r++)
                data[index++] = ecData[r][i];

        return data;
    }

    // ---- Padroes fixos (finder / timing / alignment / format / version) ----
    private static void SetupFinderPattern(BitMatrix matrix, int version)
    {
        int size = matrix.Size;
        int[][] positions = new int[][] { new[] { 0, 0 }, new[] { size - 7, 0 }, new[] { 0, size - 7 } };

        foreach (int[] pos in positions)
        {
            int row = pos[0], col = pos[1];
            for (int r = -1; r <= 7; r++)
            {
                if (row + r <= -1 || size <= row + r) continue;
                for (int c = -1; c <= 7; c++)
                {
                    if (col + c <= -1 || size <= col + c) continue;
                    bool dark = (r >= 0 && r <= 6 && (c == 0 || c == 6)) ||
                                (c >= 0 && c <= 6 && (r == 0 || r == 6)) ||
                                (r >= 2 && r <= 4 && c >= 2 && c <= 4);
                    matrix.Set(row + r, col + c, dark, true);
                }
            }
        }
    }

    private static void SetupTimingPattern(BitMatrix matrix)
    {
        int size = matrix.Size;
        for (int r = 8; r < size - 8; r++)
        {
            bool value = r % 2 == 0;
            matrix.Set(r, 6, value, true);
            matrix.Set(6, r, value, true);
        }
    }

    private static int[] GetAlignmentRowColCoords(int version)
    {
        if (version == 1) return new int[0];
        int posCount = version / 7 + 2;
        int size = GetSymbolSize(version);
        int intervals = size == 145 ? 26 : (int)(Math.Ceiling((size - 13) / (double)(2 * posCount - 2)) * 2);
        int[] positions = new int[posCount];
        positions[0] = size - 7;
        for (int i = 1; i < posCount - 1; i++) positions[i] = positions[i - 1] - intervals;
        // ultima posicao (indice posCount-1) e' sempre 6; o array e' invertido no final (igual a lib original)
        int[] full = new int[posCount];
        Array.Copy(positions, full, posCount - 1);
        full[posCount - 1] = 6;
        Array.Reverse(full);
        return full;
    }

    private static void SetupAlignmentPattern(BitMatrix matrix, int version)
    {
        int[] pos = GetAlignmentRowColCoords(version);
        int len = pos.Length;
        for (int i = 0; i < len; i++)
        {
            for (int j = 0; j < len; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == len - 1) || (i == len - 1 && j == 0)) continue;
                int row = pos[i], col = pos[j];
                for (int r = -2; r <= 2; r++)
                {
                    for (int c = -2; c <= 2; c++)
                    {
                        bool dark = r == -2 || r == 2 || c == -2 || c == 2 || (r == 0 && c == 0);
                        matrix.Set(row + r, col + c, dark, true);
                    }
                }
            }
        }
    }

    private const int G18 = (1 << 12) | (1 << 11) | (1 << 10) | (1 << 9) | (1 << 8) | (1 << 5) | (1 << 2) | (1 << 0);
    private const int G15 = (1 << 10) | (1 << 8) | (1 << 5) | (1 << 4) | (1 << 2) | (1 << 1) | (1 << 0);
    private const int G15Mask = (1 << 14) | (1 << 12) | (1 << 10) | (1 << 4) | (1 << 1);

    private static int GetVersionEncodedBits(int version)
    {
        int g18Bch = GetBchDigit(G18);
        int d = version << 12;
        while (GetBchDigit(d) - g18Bch >= 0) d ^= G18 << (GetBchDigit(d) - g18Bch);
        return (version << 12) | d;
    }

    private static int GetFormatEncodedBits(char level, int mask)
    {
        int g15Bch = GetBchDigit(G15);
        int data = (EccFormatBit(level) << 3) | mask;
        int d = data << 10;
        while (GetBchDigit(d) - g15Bch >= 0) d ^= G15 << (GetBchDigit(d) - g15Bch);
        return ((data << 10) | d) ^ G15Mask;
    }

    private static void SetupVersionInfo(BitMatrix matrix, int version)
    {
        int size = matrix.Size;
        int bits = GetVersionEncodedBits(version);
        for (int i = 0; i < 18; i++)
        {
            int row = i / 3;
            int col = i % 3 + size - 8 - 3;
            bool mod = ((bits >> i) & 1) == 1;
            matrix.Set(row, col, mod, true);
            matrix.Set(col, row, mod, true);
        }
    }

    private static void SetupFormatInfo(BitMatrix matrix, char level, int maskPattern)
    {
        int size = matrix.Size;
        int bits = GetFormatEncodedBits(level, maskPattern);
        for (int i = 0; i < 15; i++)
        {
            bool mod = ((bits >> i) & 1) == 1;
            if (i < 6) matrix.Set(i, 8, mod, true);
            else if (i < 8) matrix.Set(i + 1, 8, mod, true);
            else matrix.Set(size - 15 + i, 8, mod, true);

            if (i < 8) matrix.Set(8, size - i - 1, mod, true);
            else if (i < 9) matrix.Set(8, 15 - i - 1 + 1, mod, true);
            else matrix.Set(8, 15 - i - 1, mod, true);
        }
        matrix.Set(size - 8, 8, true, true);
    }

    private static void SetupData(BitMatrix matrix, byte[] data)
    {
        int size = matrix.Size;
        int inc = -1;
        int row = size - 1;
        int bitIndex = 7;
        int byteIndex = 0;

        for (int col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col--;
            while (true)
            {
                for (int c = 0; c < 2; c++)
                {
                    if (!matrix.IsReserved(row, col - c))
                    {
                        bool dark = false;
                        if (byteIndex < data.Length) dark = ((data[byteIndex] >> bitIndex) & 1) == 1;
                        matrix.Set(row, col - c, dark, false);
                        bitIndex--;
                        if (bitIndex == -1) { byteIndex++; bitIndex = 7; }
                    }
                }
                row += inc;
                if (row < 0 || size <= row) { row -= inc; inc = -inc; break; }
            }
        }
    }

    // ---- Mascara (escolhe o padrao que minimiza penalidade visual, conforme spec) ----
    private static bool GetMaskAt(int pattern, int i, int j)
    {
        switch (pattern)
        {
            case 0: return (i + j) % 2 == 0;
            case 1: return i % 2 == 0;
            case 2: return j % 3 == 0;
            case 3: return (i + j) % 3 == 0;
            case 4: return (i / 2 + j / 3) % 2 == 0;
            case 5: return (i * j) % 2 + (i * j) % 3 == 0;
            case 6: return ((i * j) % 2 + (i * j) % 3) % 2 == 0;
            case 7: return ((i * j) % 3 + (i + j) % 2) % 2 == 0;
            default: throw new Exception("Padrao de mascara invalido: " + pattern);
        }
    }

    private static void ApplyMask(int pattern, BitMatrix data)
    {
        int size = data.Size;
        for (int col = 0; col < size; col++)
            for (int row = 0; row < size; row++)
            {
                if (data.IsReserved(row, col)) continue;
                data.Xor(row, col, GetMaskAt(pattern, row, col));
            }
    }

    private static int GetPenaltyN1(BitMatrix data)
    {
        int size = data.Size;
        int points = 0;
        for (int row = 0; row < size; row++)
        {
            int sameCountCol = 0, sameCountRow = 0;
            bool? lastCol = null, lastRow = null;
            for (int col = 0; col < size; col++)
            {
                bool moduleCol = data.Get(row, col);
                if (lastCol.HasValue && moduleCol == lastCol.Value) sameCountCol++;
                else { if (sameCountCol >= 5) points += 3 + (sameCountCol - 5); lastCol = moduleCol; sameCountCol = 1; }

                bool moduleRow = data.Get(col, row);
                if (lastRow.HasValue && moduleRow == lastRow.Value) sameCountRow++;
                else { if (sameCountRow >= 5) points += 3 + (sameCountRow - 5); lastRow = moduleRow; sameCountRow = 1; }
            }
            if (sameCountCol >= 5) points += 3 + (sameCountCol - 5);
            if (sameCountRow >= 5) points += 3 + (sameCountRow - 5);
        }
        return points;
    }

    private static int GetPenaltyN2(BitMatrix data)
    {
        int size = data.Size;
        int points = 0;
        for (int row = 0; row < size - 1; row++)
            for (int col = 0; col < size - 1; col++)
            {
                int last = (data.Get(row, col) ? 1 : 0) + (data.Get(row, col + 1) ? 1 : 0) +
                           (data.Get(row + 1, col) ? 1 : 0) + (data.Get(row + 1, col + 1) ? 1 : 0);
                if (last == 4 || last == 0) points++;
            }
        return points * 3;
    }

    private static int GetPenaltyN3(BitMatrix data)
    {
        int size = data.Size;
        int points = 0;
        for (int row = 0; row < size; row++)
        {
            int bitsCol = 0, bitsRow = 0;
            for (int col = 0; col < size; col++)
            {
                bitsCol = ((bitsCol << 1) & 0x7FF) | (data.Get(row, col) ? 1 : 0);
                if (col >= 10 && (bitsCol == 0x5D0 || bitsCol == 0x05D)) points++;

                bitsRow = ((bitsRow << 1) & 0x7FF) | (data.Get(col, row) ? 1 : 0);
                if (col >= 10 && (bitsRow == 0x5D0 || bitsRow == 0x05D)) points++;
            }
        }
        return points * 40;
    }

    private static int GetPenaltyN4(BitMatrix data)
    {
        int darkCount = 0;
        int modulesCount = data.Data.Length;
        for (int i = 0; i < modulesCount; i++) if (data.Data[i]) darkCount++;
        int k = Math.Abs((int)Math.Ceiling(darkCount * 100.0 / modulesCount / 5) - 10);
        return k * 10;
    }

    private static int GetBestMask(BitMatrix data, char level)
    {
        int bestPattern = 0;
        int lowerPenalty = int.MaxValue;
        for (int p = 0; p < 8; p++)
        {
            SetupFormatInfo(data, level, p);
            ApplyMask(p, data);
            int penalty = GetPenaltyN1(data) + GetPenaltyN2(data) + GetPenaltyN3(data) + GetPenaltyN4(data);
            ApplyMask(p, data); // desfaz (XOR e' a propria inversa)
            if (penalty < lowerPenalty) { lowerPenalty = penalty; bestPattern = p; }
        }
        return bestPattern;
    }

    // ---- Entrada publica ----
    // Gera a matriz de modulos (true = modulo escuro) pros bytes fornecidos, tentando os
    // niveis de correcao de erro em ordem de preferencia (mais redundante primeiro) e a menor
    // versao que couber, ate maxVersion. Lanca excecao so' se nem o nivel L couber ate
    // maxVersion (na pratica, nunca deve acontecer pra um token/URL de validacao de ingresso).
    public static bool[,] Encode(byte[] data, out int size, char[] eccPreference = null, int maxVersion = 20)
    {
        if (eccPreference == null) eccPreference = new[] { 'Q', 'M', 'L' };

        int version = 0;
        char level = 'L';
        foreach (char lvl in eccPreference)
        {
            int v = FindVersion(data.Length, lvl, maxVersion);
            if (v > 0) { version = v; level = lvl; break; }
        }
        if (version == 0)
            throw new Exception(string.Format("QR: dados grandes demais ({0} bytes) para caber ate a versao {1}.", data.Length, maxVersion));

        byte[] codewords = CreateData(version, level, data);

        int moduleCount = GetSymbolSize(version);
        BitMatrix matrix = new BitMatrix(moduleCount);

        SetupFinderPattern(matrix, version);
        SetupTimingPattern(matrix);
        SetupAlignmentPattern(matrix, version);
        SetupFormatInfo(matrix, level, 0);
        if (version >= 7) SetupVersionInfo(matrix, version);
        SetupData(matrix, codewords);

        int bestMask = GetBestMask(matrix, level);
        ApplyMask(bestMask, matrix);
        SetupFormatInfo(matrix, level, bestMask);

        size = moduleCount;
        bool[,] outMatrix = new bool[moduleCount, moduleCount];
        for (int r = 0; r < moduleCount; r++)
            for (int c = 0; c < moduleCount; c++)
                outMatrix[r, c] = matrix.Get(r, c);
        return outMatrix;
    }
}
