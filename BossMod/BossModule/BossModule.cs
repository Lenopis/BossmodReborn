﻿using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BossMod
{
    // base for boss modules - provides all the common features, so that look is standardized
    public class BossModule : IDisposable
    {
        public WorldState WorldState { get; init; }
        public StateMachine StateMachine { get; init; } = new();
        public StateMachine.State? InitialState = null;
        public MiniArena Arena { get; init; } = new();

        public WorldState.Actor?[] RaidMembers; // this is fixed-size, but some slots could be empty; when player is removed, gap is created - existing players keep their indices
        public int PlayerSlot { get; private set; } = -1;
        public WorldState.Actor? RaidMember(int slot) => (slot >= 0 && slot < RaidMembers.Length) ? RaidMembers[slot] : null; // bounds-checking accessor
        public WorldState.Actor? Player() => RaidMember(PlayerSlot);

        // per-oid enemy lists; filled on first request
        private Dictionary<uint, List<WorldState.Actor>> _relevantEnemies = new(); // key = actor OID
        public IReadOnlyDictionary<uint, List<WorldState.Actor>> RelevantEnemies => _relevantEnemies;
        public List<WorldState.Actor> Enemies<OID>(OID oid) where OID : Enum
        {
            var castOID = (uint)(object)oid;
            var entry = _relevantEnemies.GetValueOrDefault(castOID);
            if (entry == null)
            {
                entry = new();
                foreach (var actor in WorldState.Actors.Values.Where(actor => actor.OID == castOID))
                    entry.Add(actor);
                _relevantEnemies[castOID] = entry;
            }
            return entry;
        }

        // list of actor-specific hints (string + whether this is a "risk" type of hint)
        public class TextHints : List<(string, bool)>
        {
            public void Add(string text, bool isRisk = true) => base.Add((text, isRisk));
        }

        // list of actor-specific "movement hints" (arrow start/end pos + color)
        public class MovementHints : List<(Vector3, Vector3, uint)>
        {
            public void Add(Vector3 from, Vector3 to, uint color) => base.Add((from, to, color));
        }

        // different encounter mechanics can be split into independent components
        // individual components should be activated and deactivated when needed (typically by state machine transitions)
        public class Component
        {
            public virtual void Update() { } // called every frame - it is a good place to update any cached values
            public virtual void AddHints(int slot, WorldState.Actor actor, TextHints hints, MovementHints? movementHints) { } // gather any relevant pieces of advice for specified raid member
            public virtual void DrawArenaBackground(MiniArena arena) { } // called at the beginning of arena draw, good place to draw aoe zones
            public virtual void DrawArenaForeground(MiniArena arena) { } // called after arena background and borders are drawn, good place to draw actors, tethers, etc.

            // world state event handlers
            public virtual void OnStatusGain(WorldState.Actor actor, int index) { }
            public virtual void OnStatusLose(WorldState.Actor actor, int index) { }
            public virtual void OnStatusChange(WorldState.Actor actor, int index) { }
            public virtual void OnTethered(WorldState.Actor actor) { }
            public virtual void OnUntethered(WorldState.Actor actor) { }
            public virtual void OnCastStarted(WorldState.Actor actor) { }
            public virtual void OnCastFinished(WorldState.Actor actor) { }
            public virtual void OnEventCast(WorldState.CastResult info) { }
            public virtual void OnEventIcon(uint actorID, uint iconID) { }
            public virtual void OnEventEnvControl(uint featureID, byte index, uint state) { }
        }
        private List<Component> _components = new();
        public IReadOnlyList<Component> Components => _components;

        protected T ActivateComponent<T>(T comp) where T : Component
        {
            if (FindComponent<T>() != null)
            {
                Service.Log($"[BossModule] Activating a component of type {comp.GetType()} when another of the same type is already active; old one is deactivated automatically");
                DeactivateComponent<T>();
            }
            _components.Add(comp);
            foreach (var actor in WorldState.Actors.Values)
            {
                if (actor.CastInfo != null)
                    comp.OnCastStarted(actor);
                if (actor.Tether.ID != 0)
                    comp.OnTethered(actor);
                for (int i = 0; i < actor.Statuses.Length; ++i)
                    if (actor.Statuses[i].ID != 0)
                        comp.OnStatusGain(actor, i);
            }
            return comp;
        }

        protected void DeactivateComponent<T>() where T : Component
        {
            int count = _components.RemoveAll(x => x is T);
            if (count == 0)
                Service.Log($"[BossModule] Could not find a component of type {typeof(T)} to deactivate");
        }

        public T? FindComponent<T>() where T : Component
        {
            return _components.OfType<T>().FirstOrDefault();
        }

        public BossModule(WorldState w, int maxRaidMembers)
        {
            WorldState = w;
            RaidMembers = new WorldState.Actor?[maxRaidMembers];

            WorldState.PlayerActorIDChanged += PlayerIDChanged;
            WorldState.PlayerInCombatChanged += EnterExitCombat;
            WorldState.ActorCreated += OnActorCreated;
            WorldState.ActorDestroyed += OnActorDestroyed;
            WorldState.ActorCastStarted += OnActorCastStarted;
            WorldState.ActorCastFinished += OnActorCastFinished;
            WorldState.ActorTethered += OnActorTethered;
            WorldState.ActorUntethered += OnActorUntethered;
            WorldState.ActorStatusGain += OnActorStatusGain;
            WorldState.ActorStatusLose += OnActorStatusLose;
            WorldState.ActorStatusChange += OnActorStatusChange;
            WorldState.EventIcon += OnEventIcon;
            WorldState.EventCast += OnEventCast;
            WorldState.EventEnvControl += OnEventEnvControl;
            foreach (var v in WorldState.Actors)
                OnActorCreated(null, v.Value);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                WorldState.PlayerActorIDChanged -= PlayerIDChanged;
                WorldState.PlayerInCombatChanged -= EnterExitCombat;
                WorldState.ActorCreated -= OnActorCreated;
                WorldState.ActorDestroyed -= OnActorDestroyed;
                WorldState.ActorCastStarted -= OnActorCastStarted;
                WorldState.ActorCastFinished -= OnActorCastFinished;
                WorldState.ActorTethered -= OnActorTethered;
                WorldState.ActorUntethered -= OnActorUntethered;
                WorldState.ActorStatusGain -= OnActorStatusGain;
                WorldState.ActorStatusLose -= OnActorStatusLose;
                WorldState.ActorStatusChange -= OnActorStatusChange;
                WorldState.EventIcon -= OnEventIcon;
                WorldState.EventCast -= OnEventCast;
                WorldState.EventEnvControl -= OnEventEnvControl;
            }
        }

        public virtual void Reset()
        {
            _components.Clear();
            ResetModule();
        }

        public virtual void Update()
        {
            StateMachine.Update(WorldState.CurrentTime);
            UpdateModule();
            foreach (var comp in _components)
                comp.Update();
        }

        public virtual void Draw(float cameraAzimuth, MovementHints? pcMovementHints)
        {
            StateMachine.Draw();
            DrawHintForPlayer(pcMovementHints);
            Arena.Begin(cameraAzimuth);
            DrawArena();
            Arena.End();

            if (ImGui.Button("Show timeline"))
            {
                var timeline = new StateMachineVisualizer(InitialState);
                var w = WindowManager.CreateWindow($"{GetType()} Timeline", () => timeline.Draw(StateMachine), () => { });
                w.SizeHint = new(600, 600);
                w.MinSize = new(100, 100);
            }
            ImGui.SameLine();
            if (ImGui.Button("Force transition") && StateMachine.ActiveState?.Next != null)
            {
                StateMachine.ActiveState = StateMachine.ActiveState.Next;
            }
        }

        public virtual void DrawArena()
        {
            DrawArenaBackground();
            foreach (var comp in _components)
                comp.DrawArenaBackground(Arena);
            Arena.Border();
            DrawArenaForegroundPre();
            foreach (var comp in _components)
                comp.DrawArenaForeground(Arena);
            DrawArenaForegroundPost();
        }

        public TextHints CalculateHintsForRaidMember(int slot, WorldState.Actor actor, MovementHints? movementHints = null)
        {
            TextHints hints = new();
            foreach (var comp in _components)
                comp.AddHints(slot, actor, hints, movementHints);
            return hints;
        }

        // TODO: move to some better place...
        public static Vector3 AdjustPositionForKnockback(Vector3 pos, Vector3 origin, float distance)
        {
            return pos != origin ? pos + distance * Vector3.Normalize(pos - origin) : pos;
        }

        public static Vector3 AdjustPositionForKnockback(Vector3 pos, WorldState.Actor? source, float distance)
        {
            return source != null ? AdjustPositionForKnockback(pos, source.Position, distance) : pos;
        }

        protected virtual void ResetModule() { }
        protected virtual void UpdateModule() { }
        protected virtual void DrawArenaBackground() { } // before modules background
        protected virtual void DrawArenaForegroundPre() { } // after border, before modules foreground
        protected virtual void DrawArenaForegroundPost() { } // after modules foreground
        protected virtual void NonPlayerCreated(WorldState.Actor actor) { }
        protected virtual void NonPlayerDestroyed(WorldState.Actor actor) { }
        protected virtual void RaidMemberCreated(int index) { }
        protected virtual void RaidMemberDestroyed(int index) { } // called just before slot is cleared, so still contains old actor

        private void DrawHintForPlayer(MovementHints? movementHints)
        {
            var actor = Player();
            if (actor == null)
                return;

            var hints = CalculateHintsForRaidMember(PlayerSlot, actor, movementHints);
            var riskColor = ImGui.ColorConvertU32ToFloat4(Arena.ColorDanger);
            var safeColor = ImGui.ColorConvertU32ToFloat4(Arena.ColorSafe);
            foreach ((var hint, bool risk) in hints)
            {
                ImGui.TextColored(risk ? riskColor : safeColor, hint);
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        private void PlayerIDChanged(object? sender, uint id)
        {
            PlayerSlot = RaidMembers.FindSlot(id);
        }

        private void EnterExitCombat(object? sender, bool inCombat)
        {
            if (inCombat)
            {
                Reset();
                StateMachine.ActiveState = InitialState;
            }
            else
            {
                StateMachine.ActiveState = null;
                Reset();
            }
        }

        private void OnActorCreated(object? sender, WorldState.Actor actor)
        {
            if (actor.Type == WorldState.ActorType.Player)
            {
                int slot = Array.FindIndex(RaidMembers, x => x == null);
                if (slot != -1)
                {
                    RaidMembers[slot] = actor;
                    if (actor.InstanceID == WorldState.PlayerActorID)
                        PlayerSlot = slot;
                    RaidMemberCreated(slot);
                }
                else
                {
                    Service.Log($"[BossModule] Too many raid members: {RaidMembers.Length} already exist; skipping new actor {actor.InstanceID:X}");
                }
            }
            else
            {
                var relevant = _relevantEnemies.GetValueOrDefault(actor.OID);
                if (relevant != null)
                    relevant.Add(actor);

                NonPlayerCreated(actor);
            }
        }

        private void OnActorDestroyed(object? sender, WorldState.Actor actor)
        {
            if (actor.Type == WorldState.ActorType.Player)
            {
                int slot = RaidMembers.FindSlot(actor.InstanceID);
                if (slot != -1)
                {
                    RaidMemberDestroyed(slot);
                    RaidMembers[slot] = null;
                    if (PlayerSlot == slot)
                        PlayerSlot = -1;
                }
                else
                {
                    Service.Log($"[BossModule] Destroyed player actor {actor.InstanceID:X} not found among raid members");
                }
            }
            else
            {
                var relevant = _relevantEnemies.GetValueOrDefault(actor.OID);
                if (relevant != null)
                    relevant.Remove(actor);

                NonPlayerDestroyed(actor);
            }
        }

        private void OnActorCastStarted(object? sender, WorldState.Actor actor)
        {
            foreach (var comp in _components)
                comp.OnCastStarted(actor);
        }

        private void OnActorCastFinished(object? sender, WorldState.Actor actor)
        {
            foreach (var comp in _components)
                comp.OnCastFinished(actor);
        }

        private void OnActorTethered(object? sender, WorldState.Actor actor)
        {
            foreach (var comp in _components)
                comp.OnTethered(actor);
        }

        private void OnActorUntethered(object? sender, WorldState.Actor actor)
        {
            foreach (var comp in _components)
                comp.OnUntethered(actor);
        }

        private void OnActorStatusGain(object? sender, (WorldState.Actor actor, int index) arg)
        {
            foreach (var comp in _components)
                comp.OnStatusGain(arg.actor, arg.index);
        }

        private void OnActorStatusLose(object? sender, (WorldState.Actor actor, int index) arg)
        {
            foreach (var comp in _components)
                comp.OnStatusLose(arg.actor, arg.index);
        }

        private void OnActorStatusChange(object? sender, (WorldState.Actor actor, int index, ushort prevExtra, DateTime prevExpire) arg)
        {
            foreach (var comp in _components)
                comp.OnStatusChange(arg.actor, arg.index);
        }

        private void OnEventIcon(object? sender, (uint actorID, uint iconID) arg)
        {
            foreach (var comp in _components)
                comp.OnEventIcon(arg.actorID, arg.iconID);
        }

        private void OnEventCast(object? sender, WorldState.CastResult info)
        {
            foreach (var comp in _components)
                comp.OnEventCast(info);
        }

        private void OnEventEnvControl(object? sender, (uint featureID, byte index, uint state) arg)
        {
            foreach (var comp in _components)
                comp.OnEventEnvControl(arg.featureID, arg.index, arg.state);
        }
    }
}
