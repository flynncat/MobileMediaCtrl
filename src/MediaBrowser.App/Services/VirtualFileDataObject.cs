// 基于 Delay's blog 的 VirtualFileDataObject 实现（公共领域）的精简改写。
// 用于支持把"虚拟文件"通过 OLE 拖放传递给 Shell（资源管理器、桌面等）。
// 关键格式：
//   - CFSTR_FILEDESCRIPTORW (FileGroupDescriptorW)：描述要拖出的文件名/大小等元信息
//   - CFSTR_FILECONTENTS：每个文件的实际数据流（IStream）
//   - CFSTR_PREFERREDDROPEFFECT：默认 Copy
// Shell 在 Drop 时会枚举 FileGroupDescriptorW，再针对每个文件调用 GetData(FileContents, lindex=i)
// 拿到 IStream，按需读取数据写入目标位置。这样我们可以"边拖边按需下载"，无需预先全量下载。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using ComTypes = System.Runtime.InteropServices.ComTypes;


namespace MediaBrowser.App.Services;

/// <summary>
/// 单个虚拟文件描述。
/// </summary>
public sealed class VirtualFileDescriptor
{
    /// <summary>显示给 Shell 的文件名（含扩展名，不含路径）。</summary>
    public string Name { get; set; } = "file";

    /// <summary>文件长度（字节）。如未知可填 -1，但 Shell 进度条无法准确显示。</summary>
    public long Length { get; set; } = -1;

    /// <summary>文件最后修改时间（UTC）。可为 null。</summary>
    public DateTime? LastWriteTimeUtc { get; set; }

    /// <summary>
    /// 数据流回调：当 Shell 请求该文件内容时调用，把数据写入提供的 Stream。
    /// 该回调将在 OLE COM 线程上下文执行，请勿访问 UI 元素。
    /// </summary>
    public Action<Stream> StreamContents { get; set; } = _ => { };
}

/// <summary>
/// 虚拟文件 IDataObject，可作为 DragDrop.DoDragDrop 的数据对象。
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileDataObject : ComTypes.IDataObject
{
    // ── Shell 标准 Clipboard 格式 ──
    private static readonly short CF_FILEGROUPDESCRIPTORW =
        (short)System.Windows.DataFormats.GetDataFormat("FileGroupDescriptorW").Id;
    private static readonly short CF_FILECONTENTS =
        (short)System.Windows.DataFormats.GetDataFormat("FileContents").Id;
    private static readonly short CF_PREFERREDDROPEFFECT =
        (short)System.Windows.DataFormats.GetDataFormat("Preferred DropEffect").Id;


    private readonly List<VirtualFileDescriptor> _files;
    private readonly System.Windows.DragDropEffects _preferredEffect;

    public VirtualFileDataObject(IEnumerable<VirtualFileDescriptor> files,
        System.Windows.DragDropEffects preferredEffect = System.Windows.DragDropEffects.Copy)
    {

        _files = files?.ToList() ?? throw new ArgumentNullException(nameof(files));
        _preferredEffect = preferredEffect;
    }

    // ── IDataObject 实现 ──

    public void GetData(ref FORMATETC formatIn, out STGMEDIUM medium)
    {
        medium = default;

        if (formatIn.cfFormat == CF_FILEGROUPDESCRIPTORW &&
            (formatIn.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            try
            {
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = BuildFileGroupDescriptorW(_files);
            }
            catch
            {
                medium = default;
            }
            return;
        }

        if (formatIn.cfFormat == CF_FILECONTENTS &&
            (formatIn.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            int index = formatIn.lindex;
            if (index < 0 || index >= _files.Count)
            {
                // 越界：返回空 medium（tymed=NULL）而不抛异常，避免调试器中断
                return;
            }

            try
            {
                var stream = new VirtualFileStream(_files[index]);
                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = Marshal.GetIUnknownForObject(stream);
            }
            catch
            {
                medium = default;
            }
            return;
        }

        if (formatIn.cfFormat == CF_PREFERREDDROPEFFECT &&
            (formatIn.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            try
            {
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = BuildPreferredDropEffect(_preferredEffect);
            }
            catch
            {
                medium = default;
            }
            return;
        }

        // 不支持的格式：返回空 medium。
        // 严格说 IDataObject 协议要求返回 DV_E_FORMATETC，但抖出异常会被调试器
        // 拦截为 User-Unhandled。Shell 不依赖此返回值区分成败与否，透过 EnumFormatEtc
        // 列出的格式列表进行查询。
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
    {
        // 不支持 GetDataHere；保持 medium 不变，静默返回。
    }


    public int QueryGetData(ref FORMATETC format)
    {
        if ((format.cfFormat == CF_FILEGROUPDESCRIPTORW && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) ||
            (format.cfFormat == CF_FILECONTENTS && (format.tymed & TYMED.TYMED_ISTREAM) != 0) ||
            (format.cfFormat == CF_PREFERREDDROPEFFECT && (format.tymed & TYMED.TYMED_HGLOBAL) != 0))
        {
            return 0; // S_OK
        }
        return 1; // S_FALSE
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return unchecked((int)0x00040130); // DATA_S_SAMEFORMATETC
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        // 只读：忽略 SetData
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            throw new NotSupportedException();

        var formats = new List<FORMATETC>
        {
            new FORMATETC
            {
                cfFormat = CF_FILEGROUPDESCRIPTORW,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL
            },
            new FORMATETC
            {
                cfFormat = CF_PREFERREDDROPEFFECT,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL
            },
        };

        // 对每个文件枚举一个 FileContents 项（lindex=i）
        for (int i = 0; i < _files.Count; i++)
        {
            formats.Add(new FORMATETC
            {
                cfFormat = CF_FILECONTENTS,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = i,
                tymed = TYMED.TYMED_ISTREAM
            });
        }

        return new VirtualEnumFormatEtc(formats);
    }


    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return unchecked((int)0x80040003); // OLE_E_ADVISENOTSUPPORTED
    }

    public void DUnadvise(int connection)
    {
        // 不支持 advise，静默忽略
    }


    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return unchecked((int)0x80040003);
    }

    // ── 辅助：构建 FILEGROUPDESCRIPTORW（HGLOBAL）──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public int sizelcx;
        public int sizelcy;
        public int pointlx;
        public int pointly;
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    private const uint FD_WRITESTIME = 0x00000020;
    private const uint FD_FILESIZE = 0x00000040;
    private const uint FD_PROGRESSUI = 0x00004000;
    private const uint FD_UNICODE = 0x80000000;

    private static IntPtr BuildFileGroupDescriptorW(List<VirtualFileDescriptor> files)
    {
        // 结构：DWORD count; FILEDESCRIPTORW[count]
        int countSize = sizeof(uint);
        int descSize = Marshal.SizeOf<FILEDESCRIPTORW>();
        int total = countSize + descSize * files.Count;

        IntPtr hGlobal = Marshal.AllocHGlobal(total);
        try
        {
            Marshal.WriteInt32(hGlobal, files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                var d = new FILEDESCRIPTORW
                {
                    dwFlags = FD_PROGRESSUI,
                    cFileName = f.Name ?? "file",
                };

                if (f.Length > 0)
                {
                    // 仅在已知"真实大小"（>0）时才告诉 Shell；
                    // 对 Length=0 或未知的情况不设置 FD_FILESIZE，避免 Shell 直接当 0 字节空文件处理而跳过 IStream::Read。
                    d.dwFlags |= FD_FILESIZE;
                    d.nFileSizeLow = (uint)(f.Length & 0xFFFFFFFF);
                    d.nFileSizeHigh = (uint)((f.Length >> 32) & 0xFFFFFFFF);
                }


                if (f.LastWriteTimeUtc is DateTime t)
                {
                    d.dwFlags |= FD_WRITESTIME;
                    long ft = t.ToFileTimeUtc();
                    d.ftLastWriteTime = new System.Runtime.InteropServices.ComTypes.FILETIME
                    {
                        dwLowDateTime = (int)(ft & 0xFFFFFFFF),
                        dwHighDateTime = (int)((ft >> 32) & 0xFFFFFFFF),
                    };
                }

                IntPtr descPtr = IntPtr.Add(hGlobal, countSize + descSize * i);
                Marshal.StructureToPtr(d, descPtr, false);
            }
            return hGlobal;
        }
        catch
        {
            Marshal.FreeHGlobal(hGlobal);
            throw;
        }
    }

    private static IntPtr BuildPreferredDropEffect(System.Windows.DragDropEffects effect)
    {
        IntPtr p = Marshal.AllocHGlobal(sizeof(uint));
        Marshal.WriteInt32(p, (int)ToDropEffect(effect));
        return p;
    }

    private static uint ToDropEffect(System.Windows.DragDropEffects e)
    {
        // DROPEFFECT_NONE=0, COPY=1, MOVE=2, LINK=4
        if ((e & System.Windows.DragDropEffects.Copy) != 0) return 1;
        if ((e & System.Windows.DragDropEffects.Move) != 0) return 2;
        if ((e & System.Windows.DragDropEffects.Link) != 0) return 4;
        return 0;
    }


    // ── 内部：FORMATETC 枚举器 ──
    private sealed class VirtualEnumFormatEtc : IEnumFORMATETC
    {
        private readonly List<FORMATETC> _items;
        private int _index;

        public VirtualEnumFormatEtc(List<FORMATETC> items, int index = 0)

        {
            _items = items;
            _index = index;
        }

        public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
        {
            int fetched = 0;
            while (fetched < celt && _index < _items.Count)
            {
                rgelt[fetched++] = _items[_index++];
            }
            if (pceltFetched != null && pceltFetched.Length > 0)
                pceltFetched[0] = fetched;
            return fetched == celt ? 0 : 1; // S_OK / S_FALSE
        }

        public int Skip(int celt)
        {
            int newIndex = _index + celt;
            if (newIndex > _items.Count)
            {
                _index = _items.Count;
                return 1; // S_FALSE
            }
            _index = newIndex;
            return 0;
        }

        public int Reset()
        {
            _index = 0;
            return 0;
        }

        public void Clone(out IEnumFORMATETC newEnum)
        {
            newEnum = new VirtualEnumFormatEtc(_items, _index);
        }
    }


    // ── 内部：IStream 包装，按需调用 StreamContents 回调 ──
    [ComVisible(true)]
    private sealed class VirtualFileStream : IStream, IDisposable
    {
        // 进程级串行锁：保护 MediaDevice 等非线程安全资源。
        // Shell 在 Drop 后会多线程调用不同 IStream 的 Read，需串行化。
        private static readonly object s_streamProduceLock = new();

        private readonly VirtualFileDescriptor _descriptor;
        private FileStream? _backing;
        private string? _backingPath;
        private bool _produced;
        private bool _disposed;

        public VirtualFileStream(VirtualFileDescriptor descriptor)
        {
            _descriptor = descriptor;
        }

        private FileStream GetBuffer()
        {
            if (_produced && _backing != null)
                return _backing;

            // 串行化产出：MTP 设备不支持并发访问，同时避免多个拖拽文件互相干扰。
            lock (s_streamProduceLock)
            {
                if (_produced && _backing != null)
                    return _backing;

                DragDiagLogger.Log("VStream", $"GetBuffer 开始产出：{_descriptor.Name}");
                // 使用临时文件作为后备存储，避免大视频 OOM。
                _backingPath = Path.Combine(Path.GetTempPath(), "MMC_VFD_" + Guid.NewGuid().ToString("N"));
                FileStream tmp = new(_backingPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                    bufferSize: 1 << 16, options: FileOptions.DeleteOnClose);
                long writtenBytes = 0;
                try
                {
                    _descriptor.StreamContents(tmp);
                    tmp.Flush();
                    writtenBytes = tmp.Length;
                }
                catch (Exception ex)
                {
                    DragDiagLogger.LogError("VStream", $"StreamContents 抛异常：{_descriptor.Name}", ex);
                    // 产出失败：保留已写入部分作为不完整文件。
                    // Shell 会看到不完整文件，但总比 0 字节出错明显。
                }
                tmp.Position = 0;
                _backing = tmp;
                _produced = true;
                DragDiagLogger.Log("VStream", $"GetBuffer 完成：{_descriptor.Name}, 实际字节 {writtenBytes}");
                return _backing;
            }
        }


        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int read = 0;
            try
            {
                var buf = GetBuffer();
                read = buf.Read(pv, 0, cb);
            }
            catch
            {
                read = 0;
            }
            if (pcbRead != IntPtr.Zero)
                Marshal.WriteInt32(pcbRead, read);
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten)
        {
            if (pcbWritten != IntPtr.Zero)
                Marshal.WriteInt32(pcbWritten, 0);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long pos = 0;
            try
            {
                var buf = GetBuffer();
                SeekOrigin origin = dwOrigin switch
                {
                    0 => SeekOrigin.Begin,
                    1 => SeekOrigin.Current,
                    2 => SeekOrigin.End,
                    _ => SeekOrigin.Begin,
                };
                pos = buf.Seek(dlibMove, origin);
            }
            catch
            {
                pos = 0;
            }
            if (plibNewPosition != IntPtr.Zero)
                Marshal.WriteInt64(plibNewPosition, pos);
        }

        public void SetSize(long libNewSize) { }

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            long readTotal = 0;
            long writeTotal = 0;
            try
            {
                var buf = GetBuffer();
                long remaining = cb;
                byte[] tmp = new byte[81920];
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(remaining, tmp.Length);
                    int read = buf.Read(tmp, 0, chunk);
                    if (read <= 0) break;
                    readTotal += read;
                    IntPtr written = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        pstm.Write(tmp, read, written);
                        writeTotal += Marshal.ReadInt32(written);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(written);
                    }
                    remaining -= read;
                }
            }
            catch
            {
                // 忽略中间错误。
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, readTotal);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, writeTotal);
        }

        public void Commit(int grfCommitFlags) { }

        public void Revert() { }

        public void LockRegion(long libOffset, long cb, int dwLockType)
        {
            // 不支持锁定区域，静默忽略。
        }

        public void UnlockRegion(long libOffset, long cb, int dwLockType)
        {
            // 不支持锁定区域，静默忽略。
        }


        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            long size = _descriptor.Length;
            if (size < 0)
            {
                try { size = _backing?.Length ?? 0; } catch { size = 0; }
            }
            pstatstg = new STATSTG
            {
                type = 2, // STGTY_STREAM
                cbSize = size,
                pwcsName = (grfStatFlag & 1) == 0 ? _descriptor.Name : null!,
            };
            DragDiagLogger.Log("VStream", $"Stat 被调用：{_descriptor.Name}, 报告 size={size}");
        }


        public void Clone(out IStream ppstm)
        {
            ppstm = null!;
            // 不支持 Clone，返回 null 代替抛异常
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _backing?.Dispose(); } catch { /* 忽略 */ }
            _backing = null;
            // FileOptions.DeleteOnClose 会自动清理，但为保险起见额外尝试。
            if (!string.IsNullOrEmpty(_backingPath))
            {
                try { if (File.Exists(_backingPath)) File.Delete(_backingPath); } catch { /* 忽略 */ }
            }
        }

        ~VirtualFileStream()
        {
            Dispose();
        }
    }
}


// 临时占位（避免在文件顶部 using System.Windows）：使用全限定名 + 别名
// 该类使用 System.Windows 命名空间中的 DataFormats 与 DragDropEffects。
// 由于 .NET 在 net8.0-windows + WPF 下默认引入 PresentationCore/PresentationFramework，可直接使用。
