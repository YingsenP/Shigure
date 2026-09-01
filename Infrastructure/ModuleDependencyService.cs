using System.Text;
namespace Shigure;

internal sealed class ModuleDependencyService
{
    private static readonly string[] StateCategories =
    [
        ClassStateCatalog.CategoryState,
        ClassStateCatalog.CategoryResource,
        ClassStateCatalog.CategoryItem,
        ClassStateCatalog.CategoryConfig,
        ClassStateCatalog.CategoryTarget,
        ClassStateCatalog.CategoryFocus,
        ClassStateCatalog.CategoryMouseover,
        ClassStateCatalog.CategoryPet,
        ClassStateCatalog.CategoryBoss1,
        ClassStateCatalog.CategoryBoss2,
        ClassStateCatalog.CategoryBoss3,
        ClassStateCatalog.CategoryBoss4,
        ClassStateCatalog.CategoryBoss5
    ];

    // “锚点”是配置协议使用但不在可编辑字段目录中展示的固定字段。
    private static readonly HashSet<string> HiddenFixedStateFields = new(StringComparer.Ordinal)
    {
        "锚点"
    };

    private readonly string _classDirectory;
    private readonly string _classMacrosPath;
    private readonly object _gate = new();

    public ModuleDependencyService(string baseDirectory)
    {
        var addonRoot = Path.Combine(baseDirectory, "Fuyutsui");
        _classDirectory = Path.Combine(addonRoot, "class");
        _classMacrosPath = Path.Combine(addonRoot, "core", "classmacros.lua");
    }

    public string? Capture(ModuleDefinition module)
    {
        lock (_gate)
        {
            return CaptureCore(module);
        }
    }

    private string? CaptureCore(ModuleDefinition module)
    {
        var classId = module.Match.ClassId;
        var specId = module.Match.SpecId;
        if (classId is null || specId is null)
        {
            module.Dependencies = null;
            return "模块未同时指定职业和专精，已保存模块逻辑，但未携带配置和宏。";
        }

        var classPath = ResolveClassPath(classId.Value);
        var configDocument = ClassBlocksStore.Load(classPath);
        if (!configDocument.IsModernFormat)
        {
            throw new InvalidOperationException($"{Path.GetFileName(classPath)} 仍是旧版配置格式，无法随模块保存。");
        }

        if (!configDocument.Specs.TryGetValue(specId.Value, out var spec))
        {
            throw new InvalidOperationException($"职业 {classId} 中不存在专精 {specId} 的配置。");
        }

        var macrosDocument = ClassMacrosStore.Load(_classMacrosPath);
        var classKey = ClassMacrosStore.ToClassFileKey(classId.Value);
        if (!macrosDocument.Classes.TryGetValue(classKey, out var macros))
        {
            throw new InvalidOperationException($"classmacros.lua 中不存在职业 {classKey} 的宏配置。");
        }

        EnsureMacroCapacity(classId.Value, macros);
        var previous = module.Dependencies;
        var captured = new ModuleDependencySnapshot
        {
            ClassId = classId.Value,
            SpecId = specId.Value,
            Config = new ModuleConfigSnapshot
            {
                Spec = CaptureSpec(spec),
                SpellsList = configDocument.SpellsList.Select(entry => new ModuleSpellListEntrySnapshot
                {
                    SpellId = entry.SpellId,
                    Index = entry.Index,
                    Name = entry.Name
                }).ToList()
            },
            Macros = new ModuleMacrosSnapshot
            {
                UsesSpecDynamicSpells = macros.UsesSpecDynamicSpells,
                DynamicCommon = new List<string>(macros.DynamicCommon),
                DynamicForSpec = macros.UsesSpecDynamicSpells
                    ? new List<string>(macros.DynamicBySpec.GetValueOrDefault(specId.Value) ?? [])
                    : [],
                StaticSpells = macros.StaticSpells.Select(CaptureMacro).ToList(),
                SpecialSpells = macros.SpecialSpells.Select(CaptureMacro).ToList()
            }
        };
        PreservePreviousSpellDependencies(captured, previous);
        module.Dependencies = captured;
        return null;
    }

    private static void PreservePreviousSpellDependencies(
        ModuleDependencySnapshot captured,
        ModuleDependencySnapshot? previous)
    {
        if (previous is null || previous.ClassId != captured.ClassId || previous.SpecId != captured.SpecId)
        {
            return;
        }

        PreserveAuras(captured.Config.Spec.PlayerAuras, previous.Config.Spec.PlayerAuras);
        PreserveAuras(captured.Config.Spec.TargetHarmfulAuras, previous.Config.Spec.TargetHarmfulAuras);
        PreserveAuras(captured.Config.Spec.TargetHelpfulAuras, previous.Config.Spec.TargetHelpfulAuras);
        PreserveAuras(captured.Config.Spec.FocusHarmfulAuras, previous.Config.Spec.FocusHarmfulAuras);
        PreserveAuras(captured.Config.Spec.FocusHelpfulAuras, previous.Config.Spec.FocusHelpfulAuras);

        foreach (var incoming in previous.Config.Spec.Spells ?? [])
        {
            var local = captured.Config.Spec.Spells.FirstOrDefault(spell => spell.SpellId == incoming.SpellId);
            if (local is null)
            {
                captured.Config.Spec.Spells.Add(incoming.Clone());
                continue;
            }

            local.Charge |= incoming.Charge;
            local.ForcedKnown |= incoming.ForcedKnown;
            local.InSpellBook |= incoming.InSpellBook;
            local.MaxCharge ??= incoming.MaxCharge;
            local.CastCount ??= incoming.CastCount;
            if (string.IsNullOrWhiteSpace(local.Name))
            {
                local.Name = incoming.Name;
            }
        }

        foreach (var incoming in previous.Config.SpellsList ?? [])
        {
            if (captured.Config.SpellsList.All(spell => spell.SpellId != incoming.SpellId))
            {
                captured.Config.SpellsList.Add(incoming.Clone());
            }
        }

        PreserveMacros(captured.Macros.StaticSpells, previous.Macros.StaticSpells);
        PreserveMacros(captured.Macros.SpecialSpells, previous.Macros.SpecialSpells);

        static void PreserveAuras(List<ModuleAuraSnapshot> local, IEnumerable<ModuleAuraSnapshot>? incoming)
        {
            foreach (var aura in incoming ?? [])
            {
                var incomingIds = GetAuraSpellIds(aura.SpellId, aura.SpellIds).ToHashSet();
                var existing = local.FirstOrDefault(candidate =>
                    GetAuraSpellIds(candidate.SpellId, candidate.SpellIds).Any(incomingIds.Contains));
                if (existing is null)
                {
                    local.Add(aura.Clone());
                    continue;
                }

                foreach (var id in incomingIds)
                {
                    if (existing.SpellId != id && !existing.SpellIds.Contains(id))
                    {
                        existing.SpellIds.Add(id);
                    }
                }
                existing.MaxApps ??= aura.MaxApps;
                if (string.IsNullOrWhiteSpace(existing.Name))
                {
                    existing.Name = aura.Name;
                }
            }
        }

        static void PreserveMacros(List<ModuleMacroEntrySnapshot> local, IEnumerable<ModuleMacroEntrySnapshot>? incoming)
        {
            foreach (var macro in incoming ?? [])
            {
                if (local.All(existing => !MacroEntryEquals(existing, macro)))
                {
                    local.Add(macro.Clone());
                }
            }
        }
    }

    public ModuleDependencyImportResult Import(IReadOnlyList<ModuleDefinition> modules)
    {
        lock (_gate)
        {
            return ImportCore(modules);
        }
    }

    private ModuleDependencyImportResult ImportCore(IReadOnlyList<ModuleDefinition> modules)
    {
        var result = new ModuleDependencyImportResult();
        foreach (var module in modules
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (module.Dependencies is null)
            {
                continue;
            }

            try
            {
                ValidateSnapshot(module, module.Dependencies);
                var removedFields = RemoveUnknownStateFields(module.Dependencies.Config.Spec);
                if (removedFields.Count > 0)
                {
                    result.SanitizedModules.Add(module.Clone());
                    result.RemovedStateFields.AddRange(removedFields.Select(field =>
                        new RemovedModuleStateField(module.Name, field.Category, field.Name)));
                }

                ImportOne(module, result);
            }
            catch (Exception ex)
            {
                result.Rejected.Add(new RejectedModuleDependency(module.Id, module.Name, ex.Message));
            }
        }

        return result;
    }

    private void ImportOne(ModuleDefinition module, ModuleDependencyImportResult result)
    {
        var snapshot = module.Dependencies!;

        var classPath = ResolveClassPath(snapshot.ClassId);
        var configDocument = ClassBlocksStore.Load(classPath);
        if (!configDocument.IsModernFormat)
        {
            throw new InvalidOperationException($"{Path.GetFileName(classPath)} 仍是旧版配置格式。");
        }

        if (!configDocument.Specs.TryGetValue(snapshot.SpecId, out var localSpec))
        {
            localSpec = new ClassBlocksStore.SpecBlocks();
            configDocument.Specs[snapshot.SpecId] = localSpec;
        }

        var macrosDocument = ClassMacrosStore.Load(_classMacrosPath);
        var classKey = ClassMacrosStore.ToClassFileKey(snapshot.ClassId);
        if (!macrosDocument.Classes.TryGetValue(classKey, out var localMacros))
        {
            throw new InvalidOperationException($"classmacros.lua 中不存在职业 {classKey} 的宏配置。");
        }

        var counters = new MergeCounters();
        MergeSpec(localSpec, snapshot.Config.Spec, counters);
        MergeSpellsList(configDocument.SpellsList, snapshot.Config.SpellsList, counters);
        MergeMacros(localMacros, snapshot.SpecId, snapshot.Macros, counters);
        EnsureMacroCapacity(snapshot.ClassId, localMacros);

        if (!counters.HasConfigChanges && counters.MacrosAdded == 0)
        {
            result.Conflicts.AddRange(counters.Conflicts.Select(message => $"{module.Name}: {message}"));
            if (counters.Conflicts.Count > 0)
            {
                result.ConflictedModuleIds.Add(module.Id);
            }
            return;
        }

        CommitDocuments(configDocument, macrosDocument, counters.HasConfigChanges, counters.MacrosAdded > 0);
        result.ConfigAdded += counters.ConfigAdded;
        result.ConfigUpdated += counters.ConfigUpdated;
        result.MacrosAdded += counters.MacrosAdded;
        result.ChangedModules.Add(module.Name);
        result.Conflicts.AddRange(counters.Conflicts.Select(message => $"{module.Name}: {message}"));
        if (counters.Conflicts.Count > 0)
        {
            result.ConflictedModuleIds.Add(module.Id);
        }
    }

    private static void ValidateSnapshot(ModuleDefinition module, ModuleDependencySnapshot snapshot)
    {
        if (snapshot.SchemaVersion != ModuleDependencySnapshot.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"不支持依赖快照版本 {snapshot.SchemaVersion}。");
        }

        if (module.Match.ClassId != snapshot.ClassId || module.Match.SpecId != snapshot.SpecId)
        {
            throw new InvalidDataException("依赖快照的职业/专精与模块匹配条件不一致。");
        }

        var unknownCategory = snapshot.Config.Spec.CategorizedStates.Keys
            .FirstOrDefault(key => !StateCategories.Contains(key, StringComparer.Ordinal));
        if (unknownCategory is not null)
        {
            throw new InvalidDataException($"依赖快照包含未知状态分类“{unknownCategory}”。");
        }

        var auraGroups = new[]
        {
            snapshot.Config.Spec.PlayerAuras,
            snapshot.Config.Spec.TargetHarmfulAuras,
            snapshot.Config.Spec.TargetHelpfulAuras,
            snapshot.Config.Spec.FocusHarmfulAuras,
            snapshot.Config.Spec.FocusHelpfulAuras
        };
        if (auraGroups.SelectMany(entries => entries ?? [])
            .Any(aura => !GetAuraSpellIds(aura.SpellId, aura.SpellIds).Any()))
        {
            throw new InvalidDataException("依赖快照包含缺少有效 spellId 的光环。");
        }

        if ((snapshot.Config.Spec.Spells ?? []).Any(spell => !IsValidSpellId(spell.SpellId)))
        {
            throw new InvalidDataException("依赖快照包含缺少有效 spellId 的法术。");
        }

        if ((snapshot.Config.SpellsList ?? []).Any(spell => !IsValidSpellId(spell.SpellId)))
        {
            throw new InvalidDataException("依赖快照包含缺少有效 spellId 的技能列表条目。");
        }
    }

    private static List<RemovedStateField> RemoveUnknownStateFields(ModuleSpecSnapshot spec)
    {
        var removed = new List<RemovedStateField>();
        if (spec.NestedStates)
        {
            foreach (var (category, fields) in spec.CategorizedStates)
            {
                if (fields is null)
                {
                    continue;
                }

                for (var index = fields.Count - 1; index >= 0; index--)
                {
                    var field = fields[index]?.Trim() ?? string.Empty;
                    if (IsRecognizedStateField(category, field))
                    {
                        continue;
                    }

                    fields.RemoveAt(index);
                    if (field.Length > 0)
                    {
                        removed.Add(new RemovedStateField(category, field));
                    }
                }
            }
        }
        else
        {
            for (var index = spec.FlatStates.Count - 1; index >= 0; index--)
            {
                var field = spec.FlatStates[index]?.Trim() ?? string.Empty;
                if (HiddenFixedStateFields.Contains(field) || ClassStateCatalog.FindCategory(field) is not null)
                {
                    continue;
                }

                spec.FlatStates.RemoveAt(index);
                if (field.Length > 0)
                {
                    removed.Add(new RemovedStateField(ClassStateCatalog.CategoryState, field));
                }
            }
        }

        removed.Reverse();
        return removed;
    }

    private static bool IsRecognizedStateField(string category, string field)
        => field.Length > 0
           && (ClassStateCatalog.IsKnown(category, field)
               || string.Equals(category, ClassStateCatalog.CategoryState, StringComparison.Ordinal)
               && HiddenFixedStateFields.Contains(field));

    private string ResolveClassPath(int classId)
    {
        var path = Path.Combine(_classDirectory, ClassNames.GetConfigFileName(classId) + ".lua");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到职业配置文件: {path}", path);
        }

        if (!File.Exists(_classMacrosPath))
        {
            throw new FileNotFoundException($"找不到职业宏文件: {_classMacrosPath}", _classMacrosPath);
        }

        return path;
    }

    private static ModuleSpecSnapshot CaptureSpec(ClassBlocksStore.SpecBlocks spec) => new()
    {
        NestedStates = spec.NestedStates,
        FlatStates = new List<string>(spec.FlatStates),
        CategorizedStates = spec.CategorizedStates.ToDictionary(
            pair => pair.Key,
            pair => new List<string>(pair.Value),
            StringComparer.Ordinal),
        PlayerAuras = spec.PlayerAuras.Select(CaptureAura).ToList(),
        TargetHarmfulAuras = spec.TargetHarmfulAuras.Select(CaptureAura).ToList(),
        TargetHelpfulAuras = spec.TargetHelpfulAuras.Select(CaptureAura).ToList(),
        FocusHarmfulAuras = spec.FocusHarmfulAuras.Select(CaptureAura).ToList(),
        FocusHelpfulAuras = spec.FocusHelpfulAuras.Select(CaptureAura).ToList(),
        Spells = spec.Spells.Select(entry => new ModuleSpellSnapshot
        {
            Name = entry.Name,
            SpellId = entry.SpellId,
            Charge = entry.Charge,
            MaxCharge = entry.MaxCharge,
            CastCount = entry.CastCount,
            ForcedKnown = entry.ForcedKnown,
            InSpellBook = entry.InSpellBook
        }).ToList(),
        Group = spec.Group is null ? null : new ModuleGroupSnapshot
        {
            Num = spec.Group.Num,
            HealthPercent = spec.Group.HealthPercent,
            Role = spec.Group.Role,
            Dispel = spec.Group.Dispel,
            Auras = spec.Group.Auras.Select(entry => new ModuleGroupAuraSnapshot
            {
                Offset = entry.Offset,
                Name = entry.Name,
                SpellId = entry.SpellId,
                SpellIds = new List<long>(entry.SpellIds)
            }).ToList()
        }
    };

    private static ModuleAuraSnapshot CaptureAura(ClassBlocksStore.AuraEntry entry) => new()
    {
        Name = entry.Name,
        SpellId = entry.SpellId,
        SpellIds = new List<long>(entry.SpellIds),
        MaxApps = entry.MaxApps
    };

    private static ModuleMacroEntrySnapshot CaptureMacro(ClassMacrosStore.ArrayEntry entry) => new()
    {
        Text = entry.Text,
        Comment = entry.Comment
    };

    private static void MergeSpec(
        ClassBlocksStore.SpecBlocks local,
        ModuleSpecSnapshot incoming,
        MergeCounters counters)
    {
        if (local.NestedStates)
        {
            if (incoming.NestedStates)
            {
                foreach (var category in StateCategories)
                {
                    MergeStrings(local.CategorizedStates[category], incoming.CategorizedStates.GetValueOrDefault(category) ?? [], counters);
                }
            }
            else
            {
                MergeStrings(local.CategorizedStates[ClassStateCatalog.CategoryState], incoming.FlatStates, counters);
            }
        }
        else
        {
            if (incoming.NestedStates)
            {
                foreach (var category in StateCategories)
                {
                    MergeStrings(local.FlatStates, incoming.CategorizedStates.GetValueOrDefault(category) ?? [], counters);
                }
            }
            else
            {
                MergeStrings(local.FlatStates, incoming.FlatStates, counters);
            }
        }

        MergeAuras(local.PlayerAuras, incoming.PlayerAuras, "玩家光环", counters);
        MergeAuras(local.TargetHarmfulAuras, incoming.TargetHarmfulAuras, "目标减益", counters);
        MergeAuras(local.TargetHelpfulAuras, incoming.TargetHelpfulAuras, "目标增益", counters);
        MergeAuras(local.FocusHarmfulAuras, incoming.FocusHarmfulAuras, "焦点减益", counters);
        MergeAuras(local.FocusHelpfulAuras, incoming.FocusHelpfulAuras, "焦点增益", counters);
        MergeSpells(local.Spells, incoming.Spells, counters);
        // 队伍配置属于本地扫描布局；模块快照只为文件兼容保留，导入时不得比较或修改。
    }

    private static void MergeStrings(List<string> local, IEnumerable<string> incoming, MergeCounters counters)
    {
        var existing = new HashSet<string>(local, StringComparer.Ordinal);
        foreach (var value in incoming.Select(item => item?.Trim() ?? string.Empty).Where(item => item.Length > 0))
        {
            if (existing.Add(value))
            {
                local.Add(value);
                counters.ConfigAdded++;
            }
        }
    }

    private static void MergeAuras(
        List<ClassBlocksStore.AuraEntry> local,
        IEnumerable<ModuleAuraSnapshot> incoming,
        string label,
        MergeCounters counters)
    {
        CompactLocalAuras(local, label, counters);

        foreach (var entry in incoming)
        {
            var existing = local.FirstOrDefault(item => AuraIdentityMatches(item, entry));
            if (existing is not null)
            {
                MergeAuraMetadata(existing, entry, label, counters);
                continue;
            }

            var added = new ClassBlocksStore.AuraEntry
            {
                Name = entry.Name,
                SpellId = entry.SpellId,
                MaxApps = entry.MaxApps
            };
            added.SpellIds.AddRange(entry.SpellIds);
            local.Add(added);
            counters.ConfigAdded++;
        }
    }

    private static void CompactLocalAuras(
        List<ClassBlocksStore.AuraEntry> local,
        string label,
        MergeCounters counters)
    {
        for (var index = 0; index < local.Count; index++)
        {
            var current = local[index];
            var existing = local.Take(index).FirstOrDefault(item => AuraIdentityMatches(item, current));
            if (existing is null)
            {
                continue;
            }

            MergeAuraMetadata(existing, current, label, counters);
            local.RemoveAt(index--);
            counters.ConfigUpdated++;
        }
    }

    private static bool AuraIdentityMatches(
        ClassBlocksStore.AuraEntry left,
        ClassBlocksStore.AuraEntry right)
    {
        return SpellIdentityMatches(
            GetAuraSpellIds(left.SpellId, left.SpellIds),
            left.Name,
            GetAuraSpellIds(right.SpellId, right.SpellIds),
            right.Name);
    }

    private static bool AuraIdentityMatches(
        ClassBlocksStore.AuraEntry left,
        ModuleAuraSnapshot right)
    {
        return SpellIdentityMatches(
            GetAuraSpellIds(left.SpellId, left.SpellIds),
            left.Name,
            GetAuraSpellIds(right.SpellId, right.SpellIds),
            right.Name);
    }

    private static void MergeAuraMetadata(
        ClassBlocksStore.AuraEntry target,
        ClassBlocksStore.AuraEntry incoming,
        string label,
        MergeCounters counters)
    {
        MergeAuraMetadataCore(
            target,
            incoming.Name,
            incoming.SpellId,
            incoming.SpellIds,
            incoming.MaxApps,
            label,
            counters);
    }

    private static void MergeAuraMetadata(
        ClassBlocksStore.AuraEntry target,
        ModuleAuraSnapshot incoming,
        string label,
        MergeCounters counters)
    {
        MergeAuraMetadataCore(
            target,
            incoming.Name,
            incoming.SpellId,
            incoming.SpellIds,
            incoming.MaxApps,
            label,
            counters);
    }

    private static void MergeAuraMetadataCore(
        ClassBlocksStore.AuraEntry target,
        string? incomingName,
        long? incomingSpellId,
        IEnumerable<long>? incomingSpellIds,
        int? incomingMaxApps,
        string label,
        MergeCounters counters)
    {
        var changed = false;
        if (target.SpellId is null && incomingSpellId is > 0)
        {
            target.SpellId = incomingSpellId;
            changed = true;
        }

        foreach (var spellId in GetAuraSpellIds(incomingSpellId, incomingSpellIds))
        {
            if (target.SpellId != spellId && !target.SpellIds.Contains(spellId))
            {
                target.SpellIds.Add(spellId);
                changed = true;
            }
        }

        if (target.MaxApps is null && incomingMaxApps is not null)
        {
            target.MaxApps = incomingMaxApps;
            changed = true;
        }
        else if (target.MaxApps is not null
                 && incomingMaxApps is not null
                 && target.MaxApps != incomingMaxApps)
        {
            counters.Conflicts.Add(
                $"{label}“{DisplayName(incomingName, incomingSpellId)}”的 maxApps 存在差异："
                + $"本地 {target.MaxApps}、模块 {incomingMaxApps}，已保留本地。");
        }

        if (string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(incomingName))
        {
            target.Name = incomingName;
            changed = true;
        }

        if (changed)
        {
            counters.ConfigUpdated++;
        }
    }

    private static void MergeSpells(
        List<ClassBlocksStore.SpellEntry> local,
        IEnumerable<ModuleSpellSnapshot> incoming,
        MergeCounters counters)
    {
        CompactLocalSpells(local, counters);

        foreach (var entry in incoming)
        {
            var name = DisplayName(entry.Name, entry.SpellId);
            var existing = local.FirstOrDefault(item => SpellIdentityMatches(item, entry));
            if (existing is not null)
            {
                MergeSpellMetadata(existing, entry, name, counters);
                continue;
            }

            local.Add(new ClassBlocksStore.SpellEntry
            {
                Name = entry.Name,
                SpellId = entry.SpellId,
                Charge = entry.Charge,
                MaxCharge = entry.MaxCharge,
                CastCount = entry.CastCount,
                ForcedKnown = entry.ForcedKnown,
                InSpellBook = entry.InSpellBook
            });
            counters.ConfigAdded++;
        }
    }

    private static void CompactLocalSpells(
        List<ClassBlocksStore.SpellEntry> local,
        MergeCounters counters)
    {
        for (var index = 0; index < local.Count; index++)
        {
            var current = local[index];
            var existing = local.Take(index).FirstOrDefault(item => SpellIdentityMatches(item, current));
            if (existing is null)
            {
                continue;
            }

            MergeSpellMetadata(existing, current, DisplayName(current.Name, current.SpellId), counters);
            local.RemoveAt(index--);
            counters.ConfigUpdated++;
        }
    }

    private static void MergeSpellMetadata(
        ClassBlocksStore.SpellEntry target,
        ModuleSpellSnapshot incoming,
        string name,
        MergeCounters counters)
        => MergeSpellMetadataCore(
            target,
            incoming.Name,
            incoming.Charge,
            incoming.MaxCharge,
            incoming.CastCount,
            incoming.ForcedKnown,
            incoming.InSpellBook,
            name,
            counters);

    private static void MergeSpellMetadata(
        ClassBlocksStore.SpellEntry target,
        ClassBlocksStore.SpellEntry incoming,
        string name,
        MergeCounters counters)
        => MergeSpellMetadataCore(
            target,
            incoming.Name,
            incoming.Charge,
            incoming.MaxCharge,
            incoming.CastCount,
            incoming.ForcedKnown,
            incoming.InSpellBook,
            name,
            counters);

    private static void MergeSpellMetadataCore(
        ClassBlocksStore.SpellEntry target,
        string? incomingName,
        bool incomingCharge,
        int? incomingMaxCharge,
        int? incomingCastCount,
        bool incomingForcedKnown,
        bool incomingInSpellBook,
        string name,
        MergeCounters counters)
    {
        var changed = false;
        if (!target.Charge && incomingCharge)
        {
            target.Charge = true;
            changed = true;
        }
        if (!target.ForcedKnown && incomingForcedKnown)
        {
            target.ForcedKnown = true;
            changed = true;
        }
        if (!target.InSpellBook && incomingInSpellBook)
        {
            target.InSpellBook = true;
            changed = true;
        }

        MergeNullable(
            target.MaxCharge,
            incomingMaxCharge,
            value => target.MaxCharge = value,
            "最大充能");
        MergeNullable(
            target.CastCount,
            incomingCastCount,
            value => target.CastCount = value,
            "施法次数");

        if (string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(incomingName))
        {
            target.Name = incomingName.Trim();
            changed = true;
        }

        if (changed)
        {
            counters.ConfigUpdated++;
        }

        void MergeNullable(int? localValue, int? incomingValue, Action<int> assign, string field)
        {
            if (localValue is null && incomingValue is { } supplied)
            {
                assign(supplied);
                changed = true;
            }
            else if (localValue is not null && incomingValue is not null && localValue != incomingValue)
            {
                counters.Conflicts.Add(
                    $"法术“{name}”的{field}存在差异：本地 {localValue}、模块 {incomingValue}，已保留本地。");
            }
        }
    }

    private static bool SpellIdentityMatches(
        ClassBlocksStore.SpellEntry left,
        ClassBlocksStore.SpellEntry right)
    {
        return IsValidSpellId(left.SpellId) && left.SpellId == right.SpellId;
    }

    private static bool SpellIdentityMatches(
        ClassBlocksStore.SpellEntry left,
        ModuleSpellSnapshot right)
    {
        return IsValidSpellId(left.SpellId) && left.SpellId == right.SpellId;
    }

    private static bool SpellIdentityMatches(
        IEnumerable<long> leftIds,
        string? leftName,
        IEnumerable<long> rightIds,
        string? rightName)
    {
        var leftIdSet = leftIds.Where(IsValidSpellId).ToHashSet();
        var rightIdSet = rightIds.Where(IsValidSpellId).ToHashSet();
        return leftIdSet.Count > 0
            && rightIdSet.Count > 0
            && leftIdSet.Overlaps(rightIdSet);
    }

    private static IEnumerable<long> GetSpellIds(long spellId)
        => IsValidSpellId(spellId) ? [spellId] : [];

    private static IEnumerable<long> GetAuraSpellIds(long? spellId, IEnumerable<long>? spellIds)
    {
        var result = new HashSet<long>();
        if (spellId is { } primary && IsValidSpellId(primary))
        {
            result.Add(primary);
        }

        foreach (var id in spellIds ?? [])
        {
            if (IsValidSpellId(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static bool IsValidSpellId(long spellId) => spellId > 0;

    private static string DisplayName(string? name, long? spellId)
        => string.IsNullOrWhiteSpace(name) ? spellId?.ToString() ?? "未命名" : name.Trim();

    private static void MergeSpellsList(
        List<ClassBlocksStore.SpellsListEntry> local,
        IEnumerable<ModuleSpellListEntrySnapshot> incoming,
        MergeCounters counters)
    {
        CompactLocalSpellsList(local, counters);

        foreach (var entry in incoming)
        {
            var byId = local.FirstOrDefault(item => item.SpellId == entry.SpellId);
            if (byId is not null)
            {
                // spellId 是跨模块稳定标识；索引是当前职业文件的本地编码。
                // 同一 spellId 已存在时保留本地索引/名称，不因来源索引不同产生冲突。
                continue;
            }

            local.Add(new ClassBlocksStore.SpellsListEntry
            {
                SpellId = entry.SpellId,
                Index = entry.Index,
                Name = entry.Name,
                OriginalSpellId = 0
            });
            counters.ConfigAdded++;
        }
    }

    private static void CompactLocalSpellsList(
        List<ClassBlocksStore.SpellsListEntry> local,
        MergeCounters counters)
    {
        var seen = new HashSet<long>();
        for (var index = 0; index < local.Count; index++)
        {
            if (seen.Add(local[index].SpellId))
            {
                continue;
            }

            local.RemoveAt(index--);
            counters.ConfigUpdated++;
        }
    }

    private static void MergeMacros(
        ClassMacrosStore.ClassMacros local,
        int specId,
        ModuleMacrosSnapshot incoming,
        MergeCounters counters)
    {
        var commonNames = new HashSet<string>(local.DynamicCommon.Select(NormalizeMacroText), StringComparer.Ordinal);
        foreach (var value in incoming.DynamicCommon)
        {
            var normalized = NormalizeMacroText(value);
            if (normalized.Length > 0 && commonNames.Add(normalized))
            {
                local.DynamicCommon.Add(value.Trim());
                counters.MacrosAdded++;
            }
        }

        if (incoming.UsesSpecDynamicSpells && incoming.DynamicForSpec.Count > 0)
        {
            local.UsesSpecDynamicSpells = true;
            if (!local.DynamicBySpec.TryGetValue(specId, out var specMacros))
            {
                specMacros = new List<string>();
                local.DynamicBySpec[specId] = specMacros;
            }

            var resolved = new HashSet<string>(local.DynamicCommon.Select(NormalizeMacroText), StringComparer.Ordinal);
            resolved.UnionWith(specMacros.Select(NormalizeMacroText));
            foreach (var value in incoming.DynamicForSpec)
            {
                var normalized = NormalizeMacroText(value);
                if (normalized.Length > 0 && resolved.Add(normalized))
                {
                    specMacros.Add(value.Trim());
                    counters.MacrosAdded++;
                }
            }
        }

        var identities = BuildMacroIdentities(local);
        MergeMacroEntries(local.StaticSpells, incoming.StaticSpells, isSpecial: false, identities, counters);
        MergeMacroEntries(local.SpecialSpells, incoming.SpecialSpells, isSpecial: true, identities, counters);
    }

    private static Dictionary<MacroIdentity, ModuleMacroEntrySnapshot> BuildMacroIdentities(ClassMacrosStore.ClassMacros macros)
    {
        var result = new Dictionary<MacroIdentity, ModuleMacroEntrySnapshot>();
        foreach (var entry in macros.StaticSpells)
        {
            result.TryAdd(GetMacroIdentity(entry.Text, entry.Comment, isSpecial: false), CaptureMacro(entry));
        }
        foreach (var entry in macros.SpecialSpells)
        {
            result.TryAdd(GetMacroIdentity(entry.Text, entry.Comment, isSpecial: true), CaptureMacro(entry));
        }
        return result;
    }

    private static void MergeMacroEntries(
        List<ClassMacrosStore.ArrayEntry> local,
        IEnumerable<ModuleMacroEntrySnapshot> incoming,
        bool isSpecial,
        IDictionary<MacroIdentity, ModuleMacroEntrySnapshot> identities,
        MergeCounters counters)
    {
        foreach (var entry in incoming)
        {
            var identity = GetMacroIdentity(entry.Text, entry.Comment, isSpecial);
            if (identities.TryGetValue(identity, out var existing))
            {
                if (!MacroEntryEquals(existing, entry))
                {
                    counters.Conflicts.Add($"宏“{identity.Spell}”与本地内容不同，已保留本地。");
                }
                continue;
            }

            local.Add(new ClassMacrosStore.ArrayEntry { Text = entry.Text, Comment = entry.Comment });
            identities[identity] = entry;
            counters.MacrosAdded++;
        }
    }

    private static MacroIdentity GetMacroIdentity(string text, string? comment, bool isSpecial)
    {
        var parsed = isSpecial
            ? FuyutsuiKeymapConverter.ParseSpecialMacro(text, comment)
            : FuyutsuiKeymapConverter.ParseStaticMacro(text, comment);
        var spell = parsed.Spell.Trim();
        return spell.Length > 0
            ? new MacroIdentity(isSpecial, parsed.Unit, spell, parsed.Condition)
            : new MacroIdentity(isSpecial, parsed.Unit, NormalizeMacroText(text), parsed.Condition);
    }

    private static string NormalizeMacroText(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static void EnsureMacroCapacity(int classId, ClassMacrosStore.ClassMacros macros)
    {
        foreach (var (specId, specName) in ClassNames.GetSpecs(classId))
        {
            var dynamicCount = macros.UsesSpecDynamicSpells
                ? macros.DynamicCommon.Count + (macros.DynamicBySpec.GetValueOrDefault(specId)?.Count ?? 0)
                : macros.DynamicCommon.Count;
            var slots = checked(dynamicCount * 30 + macros.StaticSpells.Count + macros.SpecialSpells.Count);
            if (slots > FuyutsuiKeymapConverter.MacroSlotCapacity)
            {
                throw new InvalidOperationException(
                    $"宏容量超限：{ClassNames.GetClassAndSpecName(classId, specId).ClassName} {specName} 合并后 {slots} 个槽位，最大 {FuyutsuiKeymapConverter.MacroSlotCapacity}。模块未导入。");
            }
        }
    }

    private static void CommitDocuments(
        ClassBlocksStore.ClassFileDocument config,
        ClassMacrosStore.MacrosDocument macros,
        bool saveConfig,
        bool saveMacros)
    {
        var originalConfig = saveConfig ? File.ReadAllText(config.FilePath, Encoding.UTF8) : null;
        var originalMacros = saveMacros ? File.ReadAllText(macros.FilePath, Encoding.UTF8) : null;
        try
        {
            if (saveConfig)
            {
                ClassBlocksStore.Save(config);
            }
            if (saveMacros)
            {
                ClassMacrosStore.Save(macros);
            }
        }
        catch (Exception saveError)
        {
            var rollbackErrors = new List<Exception>();
            TryRestore(config.FilePath, originalConfig, rollbackErrors);
            TryRestore(macros.FilePath, originalMacros, rollbackErrors);
            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException("模块依赖写入失败，且回滚未完全成功。", [saveError, .. rollbackErrors]);
            }
            throw;
        }
    }

    private static void TryRestore(string path, string? contents, ICollection<Exception> errors)
    {
        if (contents is null)
        {
            return;
        }
        try
        {
            AtomicFile.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    private static bool SpellEquals(ClassBlocksStore.SpellEntry left, ModuleSpellSnapshot right)
        => left.SpellId == right.SpellId
           && left.Charge == right.Charge
           && left.MaxCharge == right.MaxCharge
           && left.CastCount == right.CastCount
           && left.ForcedKnown == right.ForcedKnown
           && left.InSpellBook == right.InSpellBook;

    private static bool MacroEntryEquals(ModuleMacroEntrySnapshot left, ModuleMacroEntrySnapshot right)
        => string.Equals(NormalizeMacroText(left.Text), NormalizeMacroText(right.Text), StringComparison.Ordinal)
           && string.Equals(left.Comment?.Trim(), right.Comment?.Trim(), StringComparison.Ordinal);

    private sealed class MergeCounters
    {
        public int ConfigAdded { get; set; }
        public int ConfigUpdated { get; set; }
        public int MacrosAdded { get; set; }
        public List<string> Conflicts { get; } = new();

        public bool HasConfigChanges => ConfigAdded > 0 || ConfigUpdated > 0;
    }

    // 静态宏按解析出的目标/技能/条件去重；特殊宏只按手工技能名去重，
    // 但两类宏仍是两个独立槽位。
    private readonly record struct MacroIdentity(bool IsSpecial, int Unit, string Spell, string Condition);
}

internal sealed class ModuleDependencyImportResult
{
    public int ConfigAdded { get; set; }
    public int ConfigUpdated { get; set; }
    public int MacrosAdded { get; set; }
    public List<string> ChangedModules { get; } = new();
    public List<string> Conflicts { get; } = new();
    public HashSet<string> ConflictedModuleIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RejectedModuleDependency> Rejected { get; } = new();
    public List<ModuleDefinition> SanitizedModules { get; } = new();
    public List<RemovedModuleStateField> RemovedStateFields { get; } = new();
    public bool HasChanges => ConfigAdded > 0 || ConfigUpdated > 0 || MacrosAdded > 0;
}

internal sealed record RejectedModuleDependency(string ModuleId, string ModuleName, string Reason);
internal sealed record RemovedModuleStateField(string ModuleName, string Category, string Name);
internal sealed record RemovedStateField(string Category, string Name);
