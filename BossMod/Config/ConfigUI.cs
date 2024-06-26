﻿using Dalamud.Interface.Utility.Raii;
using System.Diagnostics;
using ImGuiNET;
using System.Reflection;

namespace BossMod;

public sealed class ConfigUI : IDisposable
{
    private class UINode(ConfigNode node)
    {
        public ConfigNode Node = node;
        public string Name = "";
        public int Order;
        public UINode? Parent;
        public List<UINode> Children = [];
    }

    private readonly List<UINode> _roots = [];
    private readonly UITree _tree = new();
    private readonly ModuleViewer _mv = new();
    private readonly ConfigRoot _root;
    private readonly WorldState _ws;

    public ConfigUI(ConfigRoot config, WorldState ws)
    {
        _root = config;
        _ws = ws;

        Dictionary<Type, UINode> nodes = [];
        foreach (var n in config.Nodes)
        {
            nodes[n.GetType()] = new(n);
        }

        foreach (var (t, n) in nodes)
        {
            var props = t.GetCustomAttribute<ConfigDisplayAttribute>();
            n.Name = props?.Name ?? GenerateNodeName(t);
            n.Order = props?.Order ?? 0;
            if (props?.Parent != null)
                n.Parent = nodes.GetValueOrDefault(props.Parent);

            if (n.Parent != null)
                n.Parent.Children.Add(n);
            else
                _roots.Add(n);
        }

        SortByOrder(_roots);
    }

    public void Dispose()
    {
        _mv.Dispose();
    }

    public void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem("Configs"))
                if (tab)
                    DrawNodes(_roots);
            using (var tab = ImRaii.TabItem("Modules"))
                if (tab)
                    _mv.Draw(_tree);
            using (var tab = ImRaii.TabItem("Slash commands"))
                if (tab)
                    DrawAvailableCommands();
            using (var tab = ImRaii.TabItem("Read me!"))
                if (tab)
                    DrawReadMe();
        }
    }

    private readonly Dictionary<string, string> _availableAICommands = new()
    {
        { "on", "Enables the AI." },
        { "off", "Disables the AI." },
        { "toggle", "Toggles the AI on/off." },
        { "targetmaster", "Toggles the focus on target leader." },
        { "follow slotX", "Follows the specified slot, eg. Slot1." },
        { "follow name", "Follows the specified party member by name." },
        { "ui", "Toggles the AI menu." },
        { "forbidactions", "Toggles the forbidding of actions. (only for autorotation)" },
        { "forbidmovement", "Toggles the forbidding of movement." },
        { "followcombat", "Toggles following during combat." },
        { "followmodule", "Toggles following during active boss module." },
        { "followoutofcombat", "Toggles following during out of combat." },
        { "followtarget", "Toggles following targets during combat." },
        { "followtarget on/off", "Sets following target during combat to on or off." },
        { "positional X", "Switch to positional when following targets. (any, rear, flank, front)" }
    };

    private readonly Dictionary<string, string> _availableOtherCommands = new()
    {
        { "a", "Toggles autorotation." },
        { "d", "Opens the debug menu." },
        { "r", "Opens the replay menu." },
        { "gc", "Triggers the garbage collection." },
        { "cfg", "Lists all configs." }
    };

    private void DrawAvailableCommands()
    {
        ImGui.Text("Available Commands:");
        ImGui.Separator();
        ImGui.Text("AI:");
        ImGui.Separator();
        foreach (var command in _availableAICommands)
        {
            ImGui.Text($"/bmrai {command.Key}: {command.Value}");
        }
        ImGui.Separator();
        ImGui.Text("Other commands:");
        ImGui.Separator();
        foreach (var command in _availableOtherCommands)
        {
            ImGui.Text($"/bmr {command.Key}: {command.Value}");
        }
        ImGui.Text("/vbm can be used instead of /bmr and /vbmai can be used instead of /bmrai");
    }

    private void DrawReadMe()
    {
        ImGui.Text("Important information");
        ImGui.Separator();
        ImGui.Text("This is a FORK of veyn's BossMod (VBM).");
        ImGui.Spacing();
        ImGui.Text("Please do not ask him for any support for problems you encounter while using this fork.");
        ImGui.Spacing();
        ImGui.Text("Instead visit the Combat Reborn Discord and ask for support there:");
        RenderTextWithLink("https://discord.gg/p54TZMPnC9", new Uri("https://discord.gg/p54TZMPnC9"));
        ImGui.NewLine();
        ImGui.Text("Please also make sure to not load VBM and this fork at the same time.");
        ImGui.Spacing();
        ImGui.Text("The consequences of doing that are unexplored and unsupported.");
        ImGui.Separator();
        ImGui.Text("The AI is designed for legacy movement, make sure to turn on legacy movement\nwhile using AI.");
        ImGui.Spacing();
        ImGui.Text("It is advised to pause AutoDuty during boss modules since it can conflict with BMR AI.");
    }

    static void RenderTextWithLink(string displayText, Uri url)
    {
        ImGui.PushID(url.ToString());
        ImGui.Text(displayText);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
        var textSize = ImGui.CalcTextSize(displayText);
        var drawList = ImGui.GetWindowDrawList();
        var cursorPos = ImGui.GetCursorScreenPos();
        drawList.AddLine(cursorPos, new Vector2(cursorPos.X + textSize.X, cursorPos.Y), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 1, 1)));
        ImGui.PopID();
    }

    public static void DrawNode(ConfigNode node, ConfigRoot root, UITree tree, WorldState ws)
    {
        // draw standard properties
        foreach (var field in node.GetType().GetFields())
        {
            var props = field.GetCustomAttribute<PropertyDisplayAttribute>();
            if (props == null)
                continue;

            _ = field.GetValue(node) switch
            {
                bool v => DrawProperty(props, node, field, v),
                Enum v => DrawProperty(props, node, field, v),
                float v => DrawProperty(props, node, field, v),
                int v => DrawProperty(props, node, field, v),
                GroupAssignment v => DrawProperty(props, node, field, v, root, tree, ws),
                _ => false
            };
        }

        // draw custom stuff
        node.DrawCustom(tree, ws);
    }

    private static string GenerateNodeName(Type t) => t.Name.EndsWith("Config", StringComparison.Ordinal) ? t.Name.Remove(t.Name.Length - "Config".Length) : t.Name;

    private void SortByOrder(List<UINode> nodes)
    {
        nodes.SortBy(e => e.Order);
        foreach (var n in nodes)
            SortByOrder(n.Children);
    }

    private void DrawNodes(List<UINode> nodes)
    {
        foreach (var n in _tree.Nodes(nodes, n => new(n.Name)))
        {
            DrawNode(n.Node, _root, _tree, _ws);
            DrawNodes(n.Children);
        }
    }

    private static bool DrawProperty(PropertyDisplayAttribute props, ConfigNode node, FieldInfo member, bool v)
    {
        var combo = member.GetCustomAttribute<PropertyComboAttribute>();
        if (combo != null)
        {
            if (UICombo.Bool(props.Label, combo.Values, ref v))
            {
                member.SetValue(node, v);
                node.Modified.Fire();
            }
        }
        else
        {
            if (ImGui.Checkbox(props.Label, ref v))
            {
                member.SetValue(node, v);
                node.Modified.Fire();
            }
        }
        return true;
    }

    private static bool DrawProperty(PropertyDisplayAttribute props, ConfigNode node, FieldInfo member, Enum v)
    {
        if (UICombo.Enum(props.Label, ref v))
        {
            member.SetValue(node, v);
            node.Modified.Fire();
        }
        return true;
    }

    private static bool DrawProperty(PropertyDisplayAttribute props, ConfigNode node, FieldInfo member, float v)
    {
        var slider = member.GetCustomAttribute<PropertySliderAttribute>();
        if (slider != null)
        {
            var flags = ImGuiSliderFlags.None;
            if (slider.Logarithmic)
                flags |= ImGuiSliderFlags.Logarithmic;
            if (ImGui.DragFloat(props.Label, ref v, slider.Speed, slider.Min, slider.Max, "%.3f", flags))
            {
                member.SetValue(node, v);
                node.Modified.Fire();
            }
        }
        else
        {
            if (ImGui.InputFloat(props.Label, ref v))
            {
                member.SetValue(node, v);
                node.Modified.Fire();
            }
        }
        return true;
    }

    private static bool DrawProperty(PropertyDisplayAttribute props, ConfigNode node, FieldInfo member, int v)
    {
        var slider = member.GetCustomAttribute<PropertySliderAttribute>();
        if (slider != null)
        {
            var flags = ImGuiSliderFlags.None;
            if (slider.Logarithmic)
                flags |= ImGuiSliderFlags.Logarithmic;
            if (ImGui.DragInt(props.Label, ref v, slider.Speed, (int)slider.Min, (int)slider.Max, "%d", flags))
            {
                member.SetValue(node, v);
                node.Modified.Fire();
            }
        }
        else
        {
            if (ImGui.InputInt(props.Label, ref v))
            {
                member.SetValue(node, v);
                node.Modified.Fire();
            }
        }
        return true;
    }

    private static bool DrawProperty(PropertyDisplayAttribute props, ConfigNode node, FieldInfo member, GroupAssignment v, ConfigRoot root, UITree tree, WorldState ws)
    {
        var group = member.GetCustomAttribute<GroupDetailsAttribute>();
        if (group == null)
            return false;

        foreach (var tn in tree.Node(props.Label, false, v.Validate() ? 0xffffffff : 0xff00ffff, () => DrawPropertyContextMenu(node, member, v)))
        {
            var assignments = root.Get<PartyRolesConfig>().SlotsPerAssignment(ws.Party);
            if (ImGui.BeginTable("table", group.Names.Length + 2, ImGuiTableFlags.SizingFixedFit))
            {
                foreach (var n in group.Names)
                    ImGui.TableSetupColumn(n);
                ImGui.TableSetupColumn("----");
                ImGui.TableSetupColumn("Name");
                ImGui.TableHeadersRow();
                for (int i = 0; i < (int)PartyRolesConfig.Assignment.Unassigned; ++i)
                {
                    var r = (PartyRolesConfig.Assignment)i;
                    ImGui.TableNextRow();
                    for (int c = 0; c < group.Names.Length; ++c)
                    {
                        ImGui.TableNextColumn();
                        if (ImGui.RadioButton($"###{r}:{c}", v[r] == c))
                        {
                            v[r] = c;
                            node.Modified.Fire();
                        }
                    }
                    ImGui.TableNextColumn();
                    if (ImGui.RadioButton($"###{r}:---", v[r] < 0 || v[r] >= group.Names.Length))
                    {
                        v[r] = -1;
                        node.Modified.Fire();
                    }

                    string name = r.ToString();
                    if (assignments.Length > 0)
                        name += $" ({ws.Party[assignments[i]]?.Name})";
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(name);
                }
                ImGui.EndTable();
            }
        }
        return true;
    }

    private static void DrawPropertyContextMenu(ConfigNode node, FieldInfo member, GroupAssignment v)
    {
        foreach (var preset in member.GetCustomAttributes<GroupPresetAttribute>())
        {
            if (ImGui.MenuItem(preset.Name))
            {
                for (int i = 0; i < preset.Preset.Length; ++i)
                    v.Assignments[i] = preset.Preset[i];
                node.Modified.Fire();
            }
        }
    }
}
