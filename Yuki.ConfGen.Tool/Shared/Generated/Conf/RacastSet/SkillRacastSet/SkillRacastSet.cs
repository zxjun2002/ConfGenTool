using System.Collections.Generic;
using System.Linq;

public sealed partial class SkillRacastSet : IRacastSet
{
    public Dictionary<int, SkillsCt> SkillsCtCt { get; private set; }

    public SkillRacastSet(ConfData data)
    {
        SkillsCtCt = data.skillsCt.ToDictionary(e => e.id, e => e);
        OnAfterInit();
    }

    // 在 *.Logic.cs 中实现；未实现则无开销
    partial void OnAfterInit();
}
