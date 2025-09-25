using System.Collections.Generic;
using System.Linq;

public sealed partial class CategoryRacastSet : IRacastSet
{
    public Dictionary<int, Dictionary<int, CategoryRestraint>> CategoryRestraintCt { get; private set; }

    public CategoryRacastSet(ConfData data)
    {
        CategoryRestraintCt = data.categoryRestraint.GroupBy(e => e.selfCategory).ToDictionary(g0 => g0.Key, g0 => g0.ToDictionary(e => e.enemyCategory, e => e));
        OnAfterInit();
    }

    // 在 *.Logic.cs 中实现；未实现则无开销
    partial void OnAfterInit();
}
