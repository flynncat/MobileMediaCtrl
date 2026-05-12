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
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildFileGroupDescriptorW(_files);
            return;
        }

        if (formatIn.cfFormat == CF_FILECONTENTS &&
            (formatIn.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            int index = formatIn.lindex;
            if (index < 0 || index >= _files.Count)
                Marshal.ThrowExceptionForHR(unchecked((int)0x80040064)); // DV_E_LINDEX

            var stream = new VirtualFileStream(_files[index]);
            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = Marshal.GetIUnknownForObject(stream);
            return;
        }

        if (formatIn.cfFormat == CF_PREFERREDDROPEFFECT &&
            (formatIn.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildPreferredDropEffect(_preferredEffect);
            return;
        }

        Marshal.ThrowExceptionForHR(unchecked((int)0x80040064)); // DV_E_FORMATETC
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
    {
        Marshal.ThrowExceptionForHR(unchecked((int)0x80040064));
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
        Marshal.ThrowExceptionForHR(unchecked((int)0x80040003));
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

                if (f.Length >= 0)
                {
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
    private sealed class VirtualFileStream : IStream
    {
        private readonly VirtualFileDescriptor _descriptor;
        private MemoryStream? _buffer;
        private bool _produced;

        public VirtualFileStream(VirtualFileDescriptor descriptor)
        {
            _descriptor = descriptor;
        }

        private MemoryStream GetBuffer()
        {
            if (!_produced)
            {
                _buffer = new MemoryStream();
                try
                {
                    _descriptor.StreamContents(_buffer);
                }
                catch
                {
                    // 内容生成失败，返回空流（Shell 会显示 0 字节文件）
                }
                _buffer.Position = 0;
                _produced = true;
            }
            return _buffer!;
        }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            var buf = GetBuffer();
            int read = buf.Read(pv, 0, cb);
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
            var buf = GetBuffer();
            SeekOrigin origin = dwOrigin switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => SeekOrigin.Begin,
            };
            long pos = buf.Seek(dlibMove, origin);
            if (plibNewPosition != IntPtr.Zero)
                Marshal.WriteInt64(plibNewPosition, pos);
        }

        public void SetSize(long libNewSize) { }

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            var buf = GetBuffer();
            long remaining = cb;
            byte[] tmp = new byte[81920];
            long readTotal = 0;
            long writeTotal = 0;
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
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, readTotal);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, writeTotal);
        }

        public void Commit(int grfCommitFlags) { }

        public void Revert() { }

        public void LockRegion(long libOffset, long cb, int dwLockType)
        {
            Marshal.ThrowExceptionForHR(unchecked((int)0x80030001)); // STG_E_INVALIDFUNCTION
        }

        public void UnlockRegion(long libOffset, long cb, int dwLockType)
        {
            Marshal.ThrowExceptionForHR(unchecked((int)0x80030001));
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG
            {
                type = 2, // STGTY_STREAM
                cbSize = _descriptor.Length >= 0 ? _descriptor.Length : (_buffer?.Length ?? 0),
                pwcsName = (grfStatFlag & 1) == 0 ? _descriptor.Name : null!,
            };
        }

        public void Clone(out IStream ppstm)
        {
            ppstm = null!;
            Marshal.ThrowExceptionForHR(unchecked((int)0x80030001));
        }
    }
}

// 临时占位（避免在文件顶部 using System.Windows）：使用全限定名 + 别名
// 该类使用 System.Windows 命名空间中的 DataFormats 与 DragDropEffects。
// 由于 .NET 在 net8.0-windows + WPF 下默认引入 PresentationCore/PresentationFramework，可直接使用。
