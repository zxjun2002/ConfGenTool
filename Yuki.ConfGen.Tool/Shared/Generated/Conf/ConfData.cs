/// <summary>
/// 此代码为自动生成, 修改无意义, 重新生成会被覆盖
/// </summary>

using MemoryPack;
using System.Collections.Generic;

[MemoryPackable]
public partial class ConfData
{
    public BuffCt[] buffCt;
    public CategoryRestraint[] categoryRestraint;
    public SkillsCt[] skillsCt;
    public TestExcel[] testExcel;
}

[MemoryPackable]
public partial class BuffCt
{
    public int buffId;
    public int buffType;
    public string buffValue;
    public bool isUnique;
    public int buffTime;
}

[MemoryPackable]
public partial class CategoryRestraint
{
    public int id;
    public int selfCategory;
    public int enemyCategory;
    public int effectTyp;
    public int value;
}

[MemoryPackable]
public partial class SkillsCt
{
    public int id;
    public int skillId;
    public int level;
    public string skillVale;
    public int priority;
}

[MemoryPackable]
public partial class TestExcel
{
    public int id;
    public string desc;
}

