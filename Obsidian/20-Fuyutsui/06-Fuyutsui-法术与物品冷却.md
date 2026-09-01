---
title: Fuyutsui 法术与物品冷却
summary: 说明已知法术筛选、冷却与充能像素、横向计数条、驱散能力曲线和物品状态的共同数据流。
aliases:
  - Fuyutsui Spell Cooldown
  - Fuyutsui 法术物品
tags:
  - project/fuyutsui
  - doc/feature
  - area/cooldown
project: Fuyutsui
doc_type: feature
status: current
authority: source-derived
up:
  - "[[20-Fuyutsui/00-Fuyutsui-MOC]]"
related:
  - "[[20-Fuyutsui/03-Fuyutsui-状态块与编码入口]]"
  - "[[20-Fuyutsui/09-Fuyutsui-动作条键位扫描]]"
  - "[[40-跨项目/02-Shigure-ClassBlocks到config同步契约]]"
source_files:
  - Fuyutsui/core/spells.lua
  - Fuyutsui/core/block.lua
  - Fuyutsui/core/stateblocks.lua
  - Fuyutsui/core/events.lua
  - Fuyutsui/class/Evoker.lua
  - Fuyutsui/class/Rogue.lua
  - Fuyutsui/class/DemonHunter.lua
source_symbols:
  - Fuyutsui:UpdateSpellKnown
  - Fuyutsui:UpdateSpellCooldown
  - Fuyutsui:GetItemCount
  - Fuyutsui:UpdateItemCooldown
  - Fuyutsui:CreateAutoLayoutBar
  - Fuyutsui.spellsList
verified_at: 2026-08-19
---

# Fuyutsui 法术与物品冷却

上级：[[20-Fuyutsui/00-Fuyutsui-MOC]]

相关：[[20-Fuyutsui/03-Fuyutsui-状态块与编码入口]] · [[20-Fuyutsui/09-Fuyutsui-动作条键位扫描]] · [[40-跨项目/02-Shigure-ClassBlocks到config同步契约]]

## AI 快速摘要

> 当前专精 `ClassBlocks.spells` 决定冷却像素布局；`UpdateSpellKnown()` 筛出可用法术并构建驱散能力曲线；`UpdateSpellCooldown()` 每 0.2 秒写剩余时间和充能恢复。带 `charge=true` 的条目占两个顶部像素，`castCount/maxCharge` 另生成横向条。`Fuyutsui.spellsList` 则是宏动作序号表，不是冷却表。

## 范围与非范围

本页覆盖法术/物品状态生产和布局约束，不覆盖宏按钮创建与真实按键发送，见 [[20-Fuyutsui/09-Fuyutsui-动作条键位扫描]]、[[20-Fuyutsui/10-Fuyutsui-命令快捷按钮与存档]] 和 [[30-Shigure/08-Shigure-Keymap解析与按键发送]]。

本页按职业表的共同 schema 说明，不逐职业罗列具体技能。

## 两张法术表不可混用

| 对象 | 来源 | key/value 语义 | 消费者 |
|---|---|---|---|
| `blocks.spells` | `ClassBlocks[spec].spells` 经 `LoadPlayerBlocks()` 展开 | SpellID → 顶部冷却索引、可选充能索引及标志 | `UpdateSpellKnown()`、`UpdateSpellCooldown()` |
| `Fuyutsui.spellsList` | 职业 `ClassMacros` | SpellID → 宏/动作序号、名称等 | 一键辅助、插入法术、施法识别 |

同一个 SpellID 在两张表中可以同时出现，但 index 所处坐标系完全不同。

## 输入与输出

| 输入 | 处理 | 输出 |
|---|---|---|
| `ClassBlocks.spells` | 顺序分配 1 或 2 个主行槽位 | 冷却、充能恢复像素 |
| `ClassBlocks.items` | 按 itemId 升序分配主行槽位 | 物品冷却与可用性像素 |
| 法术书/天赋状态 | 已知法术筛选、`forcedKnown`/`inSpellBook` 例外 | 可更新集合；未知法术写 255 |
| Cooldown/Charge Duration | 经 `curve255` 求剩余时间颜色 | `B=0..255` |
| `castCount`、`maxCharge` | CountBars 自动布局 | 横向计数条 |
| 已知驱散法术 | 构建驱散能力、旧友方/敌方曲线与光环驱散过滤 | 队伍及目标/焦点驱散槽；旧曲线当前不参与单位类型输出 |
| 物品数量/冷却 API | 状态 getter 读取 | 药水、治疗石等物品状态 |

## 执行链路

```text
专精/天赋/法术变化
  -> UpdateSpellKnown()
     -> blocks.spells 过滤到本地 spells
     -> 未知项主槽/充能槽写 255
     -> 计算 defensive/offensive dispel 能力
     -> 重建旧友方/敌方曲线和 AuraContainer dispel 过滤

每 0.2 秒
  -> UpdateSpellCooldown()
     -> cooldown Duration -> curve255 -> 主槽 B
     -> charge Duration -> curve255 -> 充能槽 B
  -> UpdateItemCooldown()
     -> 物品 getter -> 状态槽
```

法术施放/充能数量的横向条由每个 StatusBar 自己监听相关事件并刷新，不依赖总事件框架逐项写条身。

## 冷却像素语义

`core/spells.lua:218-253` 的当前规则：

- 已知且可用、无冷却：`B=0`。
- 正常冷却：剩余秒数经 0..255 曲线编码，超过范围截断。
- API 报告冷却计时器禁用：备用颜色 `B=254/255`。
- 仅全局冷却（`isOnGCD`）：强制 `B=0`，避免把 GCD 当技能冷却。
- Duration/Cooldown 对象缺失：`B=1`，即 RGB 字节 255。
- 未知法术：初始化/已知检查时主槽及充能槽写 `B=1`。
- 充能槽表达“下一充能恢复的剩余时间”；没有 Charge Duration 时同样写 255，而不是 0。

因此字节 255 同时是“未知/不可正常求值”的哨兵，字节 254 是禁用冷却哨兵；消费端不应把它们当 254/255 秒的正常剩余时间。

## 已知法术与驱散能力

`UpdateCooldownSpellKnown()` 延迟执行实际筛选：

- 默认使用增强后的 `IsSpellKnown()`。
- `inSpellBook` 条目改用法术书检查。
- `forcedKnown` 可强制进入更新集合。
- 未知条目不会保留在活动集合中，并立即写哨兵。

随后按职业已学技能生成防御驱散与进攻驱散能力，形成：

- `dispelCapabilities`、`offensiveDispelCapabilities`。
- `includeDispelTypes`，供队伍 AuraContainer 过滤。
- `dispelCurve`、`target.friendCurve`、`target.enemyCurve`。其中 friend/enemy 曲线仍会重建，但当前 `UpdateUnitType()` 已改为单位 token 编号，不再读取它们。

因此法术学习变化会影响 [[20-Fuyutsui/08-Fuyutsui-光环容器本地集成]] 的驱散过滤，但不会改变 [[20-Fuyutsui/05-Fuyutsui-目标焦点与敌人数]] 的目标/焦点类型编号。

## 横向技能条

`LoadPlayerBlocks()` 收集 `castCount` 和 `maxCharge` 定义，`UpdatePlayerBarInfo()` 调用 `CreateAutoLayoutBar()`。每条按最大值预留，背景带相对位置编码，活动 StatusBar 表示当前次数，所有条后由灰色终点标记收尾。

这些条属于 `FuyutsuiCountBars`，与顶部“冷却/充能剩余时间”像素互补：一个表达离散当前数量，一个表达恢复时间。

## 物品状态

- `ClassBlocks.items[itemId]` 声明名称和 `isEquipped`，并在法术之后获得独立主行槽位。
- `GetItemRemainingTime()` 对普通物品检查背包数量，对 `isEquipped=true` 的物品检查装备状态；不存在或不可用时返回 255，无冷却时返回 0。
- `UpdateItemCooldown()` 遍历当前专精的 `blocks.items`，把各物品冷却直接写入已分配的索引。

ItemID 是版本敏感数据；新增同类物品必须更新聚合列表和相应 getter。

## 核心数据与不变量

- 同一专精的 `ClassBlocks.spells` 中 SpellID 必须唯一。
- 需要导出为 C# JSON 对象的技能名称也必须唯一；不同 SpellID 同名仍会在名称键上覆盖。
- `charge=true` 总是连续占两个主行槽；配置转换器必须同样 `index += 2`。
- `castCount/maxCharge` 的横向条顺序必须与 C# 扫描定义一致。
- `forcedKnown` 和 `inSpellBook` 改变的是筛选语义，不能只在 Lua 或只在生成器一侧增加。
- 更新事件与 0.2 秒轮询共同保证状态；单靠 `SPELL_UPDATE_COOLDOWN` 当前不会刷新全量冷却。

## 失败模式与当前风险

1. **重复 SpellID 留下死槽。** 唤魔师专精 2 在 `class/Evoker.lua:153-154` 两次声明 `374251`；后项覆盖 `blocks.spells[374251].index`，前项占用的像素永不刷新。
2. **重复名称破坏 C# 配置。** 潜行者专精 1 两个“死亡印记”（`Rogue.lua:59-60`）及恶魔猎手专精 2 两个“灵魂裂劈”（`DemonHunter.lua:121,125`）使用不同 SpellID；以名称为 JSON key 时前项会被覆盖。
3. **冷却事件未全量更新。** `SPELL_UPDATE_COOLDOWN` 当前主要打印新法术链接并更新天启骑士状态，不调用 `UpdateSpellCooldown()`；实际新鲜度依赖 0.2 秒轮询。
4. **调试输出污染聊天。** 首次遇到不同 SpellID 时会打印链接；大量战斗事件可能产生意外日志。
5. **版本敏感 ID。** 技能替换、英雄天赋和消耗品 ItemID 会随版本变化，旧表可能仍能加载但语义已错。
6. **延迟筛选竞态。** 已知法术扫描使用计时器；快速切专精时需确认回调读取的是当前 `blocks`。

## 修改影响

- 修改职业 `spells` 后必须执行 ClassBlocks→config 同步，见 [[40-跨项目/02-Shigure-ClassBlocks到config同步契约]]。
- 修改冷却哨兵、曲线或 GCD 规则时同步 Shigure 的状态判断和测试。
- 修改横向条预留/终点布局时同步 [[30-Shigure/02-Shigure-像素扫描与协议解码]]。
- 增加驱散类型或能力表时同时检查旧友方/敌方曲线、队伍 dispel 槽和 AuraContainer 过滤；不要假定旧曲线仍参与目标/焦点类型输出。
- 在合入职业表前自动检查“当前专精 SpellID 唯一”和“导出字段名唯一”。

## 源码索引

- `Fuyutsui/main.lua:128-169`：冷却槽、充能槽和横向条定义收集。
- `Fuyutsui/core/spells.lua:27-98`：`spellsList` 辅助、插入法术状态。
- `Fuyutsui/core/spells.lua:99-216`：已知法术与驱散能力。
- `Fuyutsui/core/spells.lua:218-280`：冷却、充能和物品更新。
- `Fuyutsui/core/block.lua:134-248`：横向 CountBars。
- `Fuyutsui/core/events.lua:191-201,425-435`：冷却事件与 0.2 秒轮询。
- `Fuyutsui/class/Evoker.lua:153-154`、`Rogue.lua:59-60`、`DemonHunter.lua:121-125`：当前重复定义实例。

## 知识图谱

本页使用 [[20-Fuyutsui/03-Fuyutsui-状态块与编码入口]] 的主行与 CountBars，并向 [[20-Fuyutsui/08-Fuyutsui-光环容器本地集成]] 提供驱散过滤；目标/焦点类型编号由 [[20-Fuyutsui/05-Fuyutsui-目标焦点与敌人数]] 独立定义，其配置同步边界由 [[40-跨项目/02-Shigure-ClassBlocks到config同步契约]] 管理。
