using System.IO;
using MemoryPack;

namespace Yuki.ConfGen.Tool.Util;

public class FileBytesUtil
{
    public static void SaveConfDataToByteFile<T>(T conf, string filePath)
    {
        byte[] byteData = MemoryPackSerializer.Serialize(conf);
        File.WriteAllBytes(filePath, byteData);
    }

    public static T LoadConfFromBinary<T>(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);
        byte[] byteData = File.ReadAllBytes(filePath);
        return MemoryPackSerializer.Deserialize<T>(byteData)!;
    }
}