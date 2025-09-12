using System.Collections.Generic;
using System.Linq;

public struct BuffRacastSet : IRacastSet
{
    public Dictionary<int, BuffCt> BuffCtCt { get; private set; }
    public Dictionary<int, EffectCt> EffectCtCt { get; private set; }

    public BuffRacastSet(ConfData data)
    {
        BuffCtCt = data.buffCt.ToDictionary(e => e.id);
        EffectCtCt = data.effectCt.ToDictionary(e => e.id);
    }
}
