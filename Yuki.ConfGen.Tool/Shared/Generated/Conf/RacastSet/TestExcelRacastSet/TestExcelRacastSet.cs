using System.Collections.Generic;
using System.Linq;

public sealed partial class TestExcelRacastSet : IRacastSet
{
    public Dictionary<int, TestExcel> TestExcelCt { get; private set; }

    public TestExcelRacastSet(ConfData data)
    {
        TestExcelCt = data.testExcel.ToDictionary(e => e.id, e => e);
        OnAfterInit();
    }

    // 在 *.Logic.cs 中实现；未实现则无开销
    partial void OnAfterInit();
}
