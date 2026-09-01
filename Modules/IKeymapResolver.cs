namespace Shigure;

public interface IKeymapResolver
{
    void SelectForClass(int? classId, int? specId);

    string? GetHotkey(int? unit, string spell, string? macroCondition = null);

    string? GetHotkey(int? unit, long spellId, string? macroCondition = null);

    IReadOnlyDictionary<int, long> GetCurrentFailedSpells();

    IReadOnlyDictionary<int, long> GetCurrentOneKeySpells();

    IReadOnlyDictionary<int, long> GetCurrentInsertItems();

    IReadOnlyDictionary<long, int> GetCurrentSpellIndices();

    IReadOnlyDictionary<long, string> GetCurrentSpellNames();

    IReadOnlyDictionary<long, int> GetCurrentItemIndices();

    IReadOnlyDictionary<long, string> GetCurrentItemNames();
}
