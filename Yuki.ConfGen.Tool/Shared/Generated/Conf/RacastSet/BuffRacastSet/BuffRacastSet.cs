using System.Collections.Generic;
using System.Linq;

public sealed partial class BuffRacastSet : IRacastSet
{
    public Dictionary<int, BuffCt> BuffCtCt { get; private set; }

    public BuffRacastSet(ConfData data)
    {
        BuffCtCt = data.buffCt.ToDictionary(e => e.buffId, e => e);
        OnAfterInit();
    }

    // 在 *.Logic.cs 中实现；未实现则无开销
    partial void OnAfterInit();
}
