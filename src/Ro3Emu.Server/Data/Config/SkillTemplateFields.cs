namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// Field names of the client's skill show data: a skill template (data/skills/skill_N.json) and its EffectData rows
/// (Lua_Config_EffectData). They are not config tables, so they have no views; their names live here, not in the code.
/// </summary>
internal static class SkillTemplateFields
{
    public const string Shows = "m_kShows", Points = "m_kPoints", Point = "m_kPoint", WarningRange = "m_kWarningRange";
    public const string ShapeType = "m_eShapeType", Radius = "m_fRadius", InnerRadius = "m_fInnerRadius";
    public const string OuterRadius = "m_fOuterRadius", Duration = "m_fDuration";
}
