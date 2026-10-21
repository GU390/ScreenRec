using System.IO;
using System.Text;

namespace ScreenRec;

/// <summary>
/// 极简 MJPEG / AVI 封装器。
/// 输出标准 AVI v1.0 结构：RIFF 'AVI ' → LIST hdrl(avih/strl) → LIST movi('00dc' 帧) → idx1 索引。
/// 所有 chunk 按 word(2 字节)对齐。
/// </summary>
internal sealed class AviWriter : IDisposable
{
    // 头部内固定偏移（绝对字节位置）
    private const int RiffSizeFieldAbs = 4;
    private const int AvihDataAbs = 32;
    private const int StrhDataAbs = 108;
    private const int MoviFourCcAbs = 220;
    private const int MoviDataStartAbs = 224;
    private const int HeaderLength = MoviDataStartAbs;

    private readonly FileStream _fs;
    private readonly List<(uint Offset, uint Length)> _index = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;

    private byte[] _header = Array.Empty<byte>();
    private int _maxFrameSize;
    private long _totalBytes;
    private bool _finalized;

    public AviWriter(string path, int width, int height, int fps)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);

        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        _header = BuildHeader();
        _fs.Write(_header, 0, _header.Length);
    }

    public int FrameCount => _index.Count;
    public long TotalBytes => _totalBytes;

    /// <summary>实际达成的帧率。收尾时写进头部，保证回放时间轴准确。</summary>
    public double ActualFps { get; set; }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v & 0xFF);
        b[off + 1] = (byte)((v >> 8) & 0xFF);
        b[off + 2] = (byte)((v >> 16) & 0xFF);
        b[off + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void WriteU16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)(v & 0xFF);
        b[off + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void WriteI16(byte[] b, int off, short v)
    {
        WriteU16(b, off, unchecked((ushort)v));
    }

    private static void WriteFourCc(byte[] b, int off, string s)
    {
        for (int i = 0; i < 4; i++)
        {
            b[off + i] = i < s.Length ? (byte)s[i] : (byte)0;
        }
    }

    private byte[] BuildHeader()
    {
        var ms = new MemoryStream(HeaderLength);
        var w = new BinaryWriter(ms);

        // RIFF
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)0);                 // 运行时回填：整个文件长度 - 8
        w.Write(Encoding.ASCII.GetBytes("AVI "));

        // LIST hdrl
        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write((uint)192);               // 'hdrl' 内容长度（含 fourcc）
        w.Write(Encoding.ASCII.GetBytes("hdrl"));

        // avih
        w.Write(Encoding.ASCII.GetBytes("avih"));
        w.Write((uint)56);
        var avih = new byte[56];
        WriteU32(avih, 0, (uint)(1_000_000.0 / _fps + 0.5)); // dwMicroSecPerFrame
        WriteU32(avih, 4, 0);            // dwMaxBytesPerSec（回填）
        WriteU32(avih, 8, 0);            // dwPaddingGranularity
        WriteU32(avih, 12, 0x10);        // dwFlags = AVIF_HASINDEX
        WriteU32(avih, 16, 0);           // dwTotalFrames（回填）
        WriteU32(avih, 20, 0);           // dwInitialFrames
        WriteU32(avih, 24, 1);           // dwStreams
        WriteU32(avih, 28, 0);           // dwSuggestedBufferSize（回填）
        WriteU32(avih, 32, (uint)_width);
        WriteU32(avih, 36, (uint)_height);
        w.Write(avih);

        // LIST strl
        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write((uint)116);              // 'strl' 内容长度（含 fourcc）
        w.Write(Encoding.ASCII.GetBytes("strl"));

        // strh
        w.Write(Encoding.ASCII.GetBytes("strh"));
        w.Write((uint)56);
        var strh = new byte[56];
        WriteFourCc(strh, 0, "vids");
        WriteFourCc(strh, 4, "MJPG");
        WriteU32(strh, 8, 0);            // dwFlags
        WriteU16(strh, 12, 0);           // wPriority
        WriteU16(strh, 14, 0);           // wLanguage
        WriteU32(strh, 16, 0);           // dwInitialFrames
        WriteU32(strh, 20, 1);           // dwScale
        WriteU32(strh, 24, (uint)_fps);  // dwRate
        WriteU32(strh, 28, 0);           // dwStart
        WriteU32(strh, 32, 0);           // dwLength（回填）
        WriteU32(strh, 36, 0);           // dwSuggestedBufferSize（回填）
        WriteU32(strh, 40, 0xFFFFFFFF);  // dwQuality = 默认
        WriteU32(strh, 44, 0);           // dwSampleSize
        WriteI16(strh, 48, 0);
        WriteI16(strh, 50, 0);
        WriteI16(strh, 52, (short)_width);
        WriteI16(strh, 54, (short)_height);
        w.Write(strh);

        // strf (BITMAPINFOHEADER)
        w.Write(Encoding.ASCII.GetBytes("strf"));
        w.Write((uint)40);
        var strf = new byte[40];
        WriteU32(strf, 0, 40);
        WriteU32(strf, 4, (uint)_width);
        WriteU32(strf, 8, (uint)_height);
        WriteU16(strf, 12, 1);           // biPlanes
        WriteU16(strf, 14, 24);          // biBitCount
        WriteFourCc(strf, 16, "MJPG");   // biCompression
        WriteU32(strf, 20, (uint)(_width * _height * 3)); // biSizeImage
        WriteU32(strf, 24, 0);
        WriteU32(strf, 28, 0);
        WriteU32(strf, 32, 0);
        WriteU32(strf, 36, 0);
        w.Write(strf);

        // LIST movi
        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write((uint)0);                // 回填：movi 内容长度
        w.Write(Encoding.ASCII.GetBytes("movi"));

        w.Flush();
        var bytes = ms.ToArray();
        if (bytes.Length != HeaderLength)
        {
            throw new InvalidOperationException($"AVI 头长度异常：{bytes.Length} != {HeaderLength}");
        }
        return bytes;
    }

    /// <summary>写入一帧 JPEG。</summary>
    public void AddFrame(byte[] jpeg)
    {
        if (_finalized) throw new InvalidOperationException("封装器已结束。");
        if (jpeg.Length == 0) return;

        long chunkPos = _fs.Position;
        _fs.Write(Encoding.ASCII.GetBytes("00dc"), 0, 4);
        _fs.Write(BitConverter.GetBytes((uint)jpeg.Length), 0, 4);
        _fs.Write(jpeg, 0, jpeg.Length);
        int pad = jpeg.Length & 1;
        if (pad != 0) _fs.WriteByte(0);

        _index.Add(((uint)(chunkPos - MoviDataStartAbs), (uint)jpeg.Length));
        _maxFrameSize = Math.Max(_maxFrameSize, jpeg.Length);
        _totalBytes += 8 + jpeg.Length + pad;
    }

    /// <summary>回填头部、写 idx1 索引、收尾。</summary>
    public void Finish()
    {
        if (_finalized) return;
        _finalized = true;

        long moviEnd = _fs.Position;
        uint frames = (uint)_index.Count;
        double fps = ActualFps > 0.5 ? ActualFps : _fps;
        double seconds = frames / fps;

        // ---- idx1 ----
        var idx = new byte[_index.Count * 16];
        for (int i = 0; i < _index.Count; i++)
        {
            int o = i * 16;
            WriteFourCc(idx, o, "00dc");
            WriteU32(idx, o + 4, 0x10);              // AVIIF_KEYFRAME
            WriteU32(idx, o + 8, _index[i].Offset);  // 相对 'movi' 数据起点
            WriteU32(idx, o + 12, _index[i].Length);
        }
        _fs.Write(Encoding.ASCII.GetBytes("idx1"), 0, 4);
        _fs.Write(BitConverter.GetBytes((uint)idx.Length), 0, 4);
        if (idx.Length > 0) _fs.Write(idx, 0, idx.Length);

        long fileSize = _fs.Position;

        // ---- 回填头部 ----
        uint avgBytesPerSec = seconds > 0.01 ? (uint)(_totalBytes / seconds) : 0;
        WriteU32(_header, RiffSizeFieldAbs, (uint)(fileSize - 8));
        WriteU32(_header, AvihDataAbs + 0, (uint)(1_000_000.0 / fps + 0.5)); // dwMicroSecPerFrame
        WriteU32(_header, AvihDataAbs + 4, avgBytesPerSec);
        WriteU32(_header, AvihDataAbs + 16, frames);
        WriteU32(_header, AvihDataAbs + 28, (uint)_maxFrameSize);
        WriteU32(_header, StrhDataAbs + 20, 1000);  // dwScale
        WriteU32(_header, StrhDataAbs + 24, (uint)(fps * 1000 + 0.5)); // dwRate
        WriteU32(_header, StrhDataAbs + 32, frames);
        WriteU32(_header, StrhDataAbs + 36, (uint)_maxFrameSize);
        WriteU32(_header, MoviFourCcAbs - 4, (uint)(moviEnd - MoviFourCcAbs));

        _fs.Seek(0, SeekOrigin.Begin);
        _fs.Write(_header, 0, _header.Length);
        _fs.Seek(0, SeekOrigin.End);
        _fs.Flush();
    }

    public void Dispose()
    {
        try
        {
            if (!_finalized) Finish();
        }
        catch
        {
            // ignore
        }
        _fs.Dispose();
    }
}
