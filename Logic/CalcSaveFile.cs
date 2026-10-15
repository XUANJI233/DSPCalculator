using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DSPCalculator.Logic
{
    /// <summary>
    /// 一个计算器窗口在存档中的记录
    /// </summary>
    public class CalcWindowRecord
    {
        public const byte STATE_OPEN = 0; // 正常打开
        public const byte STATE_DOCK_LEFT = 1; // 贴在左边缘（隐藏）
        public const byte STATE_DOCK_RIGHT = 2; // 贴在右边缘（隐藏）
        public const byte STATE_CLOSED = 3; // 已关闭但在后台保留（WindowsManager.closedWindows）

        public byte state;
        public int closedOrder; // 在closedWindows中的顺序，越大越晚关闭；未关闭的窗口为-1
        public float x; // windowObj.transform.localPosition
        public float y;
        public bool isLargeWindow;
        public string title; // 自定义标题，空字符串表示默认标题
        public string data; // UICalcWindow.Copy()生成的字符串

        public CalcWindowRecord()
        {
            state = STATE_OPEN;
            closedOrder = -1;
            isLargeWindow = true;
            title = "";
            data = "";
        }
    }

    /// <summary>
    /// 计算器窗口的存档附加文件（存档名.dspcalc），不依赖Unity，方便单独测试
    /// 格式：魔数、格式版本、窗口数N，然后是N条带长度前缀的窗口记录（新版本可在记录末尾追加字段，旧版本读取时会跳过）
    /// </summary>
    public static class CalcSaveFile
    {
        public const string Extension = ".dspcalc";
        public const int Magic = 0x434C4344; // "DCLC"
        public const int FormatVersion = 1;
        public const int MaxWindowCount = 1000;

        public static byte[] Serialize(List<CalcWindowRecord> records)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(records.Count);
                foreach (CalcWindowRecord rec in records)
                {
                    byte[] body = SerializeRecord(rec);
                    w.Write(body.Length);
                    w.Write(body);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        static byte[] SerializeRecord(CalcWindowRecord rec)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(rec.state);
                w.Write(rec.closedOrder);
                w.Write(rec.x);
                w.Write(rec.y);
                w.Write(rec.isLargeWindow);
                w.Write(rec.title ?? "");
                w.Write(rec.data ?? "");
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>
        /// 解析失败时抛出异常
        /// </summary>
        public static List<CalcWindowRecord> Deserialize(byte[] bytes)
        {
            List<CalcWindowRecord> records = new List<CalcWindowRecord>();
            using (MemoryStream ms = new MemoryStream(bytes))
            using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
            {
                if (r.ReadInt32() != Magic)
                    throw new InvalidDataException("Not a DSPCalculator save file.");
                int version = r.ReadInt32();
                if (version < 1)
                    throw new InvalidDataException("Unsupported DSPCalculator save file version " + version);
                int count = r.ReadInt32();
                if (count < 0 || count > MaxWindowCount)
                    throw new InvalidDataException("Invalid window count " + count);
                for (int i = 0; i < count; i++)
                {
                    int length = r.ReadInt32();
                    if (length < 0 || length > ms.Length - ms.Position)
                        throw new InvalidDataException("Invalid window record length " + length);
                    long end = ms.Position + length;
                    CalcWindowRecord rec = new CalcWindowRecord();
                    rec.state = r.ReadByte();
                    rec.closedOrder = r.ReadInt32();
                    rec.x = r.ReadSingle();
                    rec.y = r.ReadSingle();
                    rec.isLargeWindow = r.ReadBoolean();
                    rec.title = r.ReadString();
                    rec.data = r.ReadString();
                    if (ms.Position > end)
                        throw new InvalidDataException("Window record overflows its length");
                    ms.Position = end; // 跳过新版本追加的字段
                    records.Add(rec);
                }
            }
            return records;
        }

        /// <summary>
        /// 文件不存在时返回null，文件损坏时抛出异常
        /// </summary>
        public static List<CalcWindowRecord> Read(string path)
        {
            if (!File.Exists(path))
                return null;
            return Deserialize(File.ReadAllBytes(path));
        }

        /// <summary>
        /// 先写临时文件再替换，避免写到一半时游戏崩溃导致文件损坏。bytes为null时删除文件
        /// </summary>
        public static void WriteAtomic(string path, byte[] bytes)
        {
            if (bytes == null)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            string tmpPath = path + ".tmp";
            File.WriteAllBytes(tmpPath, bytes);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmpPath, path, null);
                    return;
                }
                catch (Exception)
                {
                    // 某些文件系统或运行时不支持Replace，退回到删除再移动
                }
                File.Delete(path);
            }
            File.Move(tmpPath, path);
        }

        public static string PathFor(string saveFolder, string saveName)
        {
            return Path.Combine(saveFolder, saveName + Extension);
        }
    }

    /// <summary>
    /// 游戏存档期间，记录相关存档文件在存档前的状态，存档完成后让每个.dspcalc文件跟随对应的存档文件。
    /// 游戏的自动存档会先写临时文件，再轮换_autosave_0~3等文件，这里不依赖游戏的具体轮换方式：
    /// 用最后修改时间和文件大小识别每个存档文件，存档后它被移动到哪个名字，它的.dspcalc就跟到哪个名字；
    /// 新出现的存档文件写入当前的计算器窗口数据；消失的存档文件，删除对应的.dspcalc。
    /// </summary>
    public class SaveSlotScope
    {
        class Slot
        {
            public string identity; // 存档文件的“身份”，存档文件不存在时为null
            public byte[] calcData; // 对应.dspcalc文件的内容，不存在时为null
        }

        string saveFolder;
        string saveExt;
        List<string> names;
        Dictionary<string, Slot> before;

        public SaveSlotScope(string saveFolder, string saveExt, IEnumerable<string> saveNames)
        {
            this.saveFolder = saveFolder;
            this.saveExt = saveExt;
            names = saveNames.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            before = new Dictionary<string, Slot>();
            foreach (string name in names)
            {
                Slot slot = new Slot();
                slot.identity = GetIdentity(SavePath(name));
                string calcPath = CalcSaveFile.PathFor(saveFolder, name);
                if (slot.identity != null && File.Exists(calcPath))
                    slot.calcData = File.ReadAllBytes(calcPath);
                before[name] = slot;
            }
        }

        string SavePath(string name)
        {
            return Path.Combine(saveFolder, name + saveExt);
        }

        static string GetIdentity(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists)
                return null;
            return info.LastWriteTimeUtc.Ticks.ToString() + "_" + info.Length.ToString();
        }

        /// <summary>
        /// 存档完成后调用。captureCurrent用于获取当前计算器窗口的数据（返回null表示没有窗口），只在出现了新存档文件时调用
        /// </summary>
        public void Apply(Func<byte[]> captureCurrent)
        {
            Dictionary<string, byte[]> writes = new Dictionary<string, byte[]>();
            bool currentCaptured = false;
            byte[] current = null;
            foreach (string name in names)
            {
                string identity = GetIdentity(SavePath(name));
                if (identity == null)
                {
                    writes[name] = null;
                    continue;
                }
                string from = null;
                foreach (var kv in before)
                {
                    if (kv.Value.identity == identity)
                    {
                        from = kv.Key;
                        break;
                    }
                }
                if (from == name)
                    continue; // 存档文件没有变化
                if (from != null)
                {
                    writes[name] = before[from].calcData;
                }
                else
                {
                    if (!currentCaptured)
                    {
                        current = captureCurrent();
                        currentCaptured = true;
                    }
                    writes[name] = current;
                }
            }
            foreach (var kv in writes)
            {
                CalcSaveFile.WriteAtomic(CalcSaveFile.PathFor(saveFolder, kv.Key), kv.Value);
            }
        }
    }
}
