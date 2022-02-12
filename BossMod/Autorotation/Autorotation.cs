﻿using Dalamud.Hooking;
using ImGuiNET;
using System;
using System.Collections.Generic;

namespace BossMod
{
    // typically 'casting an action' causes the following sequence of events:
    // - immediately after sending ActionRequest message, client 'speculatively' starts CD (including GCD)
    // - ~50-100ms later client receives bundle (typically one, but sometimes messages can be spread over two frames!) with ActorControlSelf[Cooldown], ActorControl[Gain/LoseEffect], AbilityN, ActorGauge, StatusEffectList
    //   new statuses have large negative duration (e.g. -30 when ST is applied) - theory: it means 'show as X, don't reduce' - TODO test?..
    // - ~600ms later client receives EventResult with normal durations
    //
    // during this 'unconfirmed' window we might be considering wrong move to be the next-best one (e.g. imagine we've just started long IR cd and don't see the effect yet - next-best might be infuriate)
    // but I don't think this matters in practice, as presumably client forbids queueing any actions while there are pending requests
    // I don't know what happens if there is no confirmation for a long time (due to ping or packet loss)
    //
    // reject scenario:
    // a relatively easy way to repro it is doing no-movement rotation, then enabling moves when PR is up and 3 charges are up; next onslaught after PR seems to be often rejected
    // it seems that game will not send another request after reject until 500ms passed since prev request
    //
    // IMPORTANT: it seems that game uses *client-side* cooldown to determine when next request can happen, here's an example:
    // - 04:51.508: request Upheaval
    // - 04:51.635: confirm Upheaval (ACS[Cooldown] = 30s)
    // - 05:21.516: request Upheaval (30.008 since prev request, 29.881 since prev response)
    // - 05:21.609: confirm Upheaval (29.974 since prev response)
    //
    // here's a list of things we do now:
    // 1. we use cooldowns as reported by ActionManager API rather than parse network messages. This (1) allows us to not rely on randomized opcodes, (2) allows us not to handle things like CD resets on wipes, actor resets on zone changes, etc.
    // 2. we convert large negative status durations to their expected values
    // 3. when there are pending actions, we don't update internal state, leaving same next-best recommendation
    class Autorotation : IDisposable
    {
        private Network _network;
        private GeneralConfig _config;
        private WindowManager.Window? _ui;

        private List<Network.PendingAction> _pendingActions = new();
        private bool _firstPendingJustCompleted = false;

        private delegate ulong GetAdjustedActionIdDelegate(byte param1, uint param2);
        private Hook<GetAdjustedActionIdDelegate> _getAdjustedActionIdHook;
        private unsafe float* _comboTimeLeft = null;
        private unsafe uint* _comboLastMove = null;

        public unsafe float ComboTimeLeft => *_comboTimeLeft;
        public unsafe uint ComboLastMove => *_comboLastMove;

        public WARActions WarActions { get; init; } = new();

        public unsafe Autorotation(Network network, GeneralConfig config)
        {
            _network = network;
            _config = config;

            _network.EventActionRequest += OnNetworkActionRequest;
            _network.EventActionEffect += OnNetworkActionEffect;
            _network.EventActorControlCancelCast += OnNetworkActionCancel;
            _network.EventActorControlSelfActionRejected += OnNetworkActionReject;

            IntPtr comboPtr = Service.SigScanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? 80 7E 21 00", 0x178);
            _comboTimeLeft = (float*)comboPtr;
            _comboLastMove = (uint*)(comboPtr + 0x4);

            var getAdjustedActionIdAddress = Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 8B F8 3B DF");
            _getAdjustedActionIdHook = new(getAdjustedActionIdAddress, new GetAdjustedActionIdDelegate(GetAdjustedActionIdDetour));
            _getAdjustedActionIdHook.Enable();
        }

        public void Dispose()
        {
            _network.EventActionRequest -= OnNetworkActionRequest;
            _network.EventActionEffect -= OnNetworkActionEffect;
            _network.EventActorControlCancelCast -= OnNetworkActionCancel;
            _network.EventActorControlSelfActionRejected -= OnNetworkActionReject;

            _getAdjustedActionIdHook.Dispose();
        }

        public void Update()
        {
            bool enabled = false;
            if (_config.AutorotationEnabled)
            {
                enabled = (Class)(Service.ClientState.LocalPlayer?.ClassJob.Id ?? 0) == Class.WAR;
            }

            if (enabled)
            {
                _getAdjustedActionIdHook.Enable();

                if (_firstPendingJustCompleted)
                {
                    WarActions.CastSucceeded(_pendingActions[0].Action);
                    _pendingActions.RemoveAt(0);
                    _firstPendingJustCompleted = false;
                }

                if (_pendingActions.Count == 0)
                {
                    WarActions.Update(ComboLastMove, ComboTimeLeft);
                }
            }
            else
            {
                _getAdjustedActionIdHook.Disable();
            }

            bool showUI = enabled && _config.AutorotationShowUI;
            if (showUI && _ui == null)
            {
                _ui = WindowManager.CreateWindow("Autorotation", () => WarActions.DrawActionHint(false), () => { });
                _ui.SizeHint = new(100, 100);
                _ui.MinSize = new(100, 100);
                _ui.Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            }
            else if (!showUI && _ui != null)
            {
                _ui?.Close();
                _ui = null;
            }
        }

        private void OnNetworkActionRequest(object? sender, Network.PendingAction action)
        {
            if (_pendingActions.Count > 0)
            {
                Log($"New action request ({PendingActionString(action)}) while {_pendingActions.Count} are pending (first = {PendingActionString(_pendingActions[0])})", true);
            }
            Log($"++ {PendingActionString(action)}");
            _pendingActions.Add(action);
        }

        private void OnNetworkActionEffect(object? sender, WorldState.CastResult action)
        {
            if (action.SourceSequence == 0 || action.CasterID != Service.ClientState.LocalPlayer?.ObjectId)
                return; // non-player-initiated

            var pa = new Network.PendingAction() { Action = action.Action, TargetID = action.MainTargetID, Sequence = action.SourceSequence };
            int index = _pendingActions.FindIndex(a => a.Sequence == action.SourceSequence);
            if (index == -1)
            {
                Log($"Unexpected action-effect ({PendingActionString(pa)}): currently {_pendingActions.Count} are pending", true);
                _pendingActions.Clear();
                _pendingActions.Add(pa);
            }
            else if (index > 0)
            {
                Log($"Unexpected action-effect ({PendingActionString(pa)}): index={index}, first={PendingActionString(_pendingActions[0])}, count={_pendingActions.Count}", true);
                _pendingActions.RemoveRange(0, index);
            }
            if (_pendingActions[0].Action != action.Action)
            {
                Log($"Request/response action mismatch: requested {PendingActionString(_pendingActions[0])}, got {PendingActionString(pa)}", true);
                _pendingActions[0] = pa;
            }
            Log($"-+ {PendingActionString(pa)}, lock={action.AnimationLockTime:f3}");
            _firstPendingJustCompleted = true;
        }

        private void OnNetworkActionCancel(object? sender, (uint actorID, uint actionID) args)
        {
            if (args.actorID != Service.ClientState.LocalPlayer?.ObjectId)
                return; // non-player-initiated

            int index = _pendingActions.FindIndex(a => a.Action.ID == args.actionID);
            if (index == -1)
            {
                Log($"Unexpected action-cancel ({args.actionID}): currently {_pendingActions.Count} are pending", true);
                _pendingActions.Clear();
            }
            else
            {
                if (index > 0)
                {
                    Log($"Unexpected action-cancel ({PendingActionString(_pendingActions[index])}): index={index}, first={PendingActionString(_pendingActions[0])}, count={_pendingActions.Count}", true);
                    _pendingActions.RemoveRange(0, index);
                }
                Log($"-- {PendingActionString(_pendingActions[0])}");
                _pendingActions.RemoveAt(0);
            }
        }

        private void OnNetworkActionReject(object? sender, (uint actorID, uint actionID, uint sourceSequence) args)
        {
            int index = args.sourceSequence != 0
                ? _pendingActions.FindIndex(a => a.Sequence == args.sourceSequence)
                : _pendingActions.FindIndex(a => a.Action.ID == args.actionID);
            if (index == -1)
            {
                Log($"Unexpected action-reject (#{args.sourceSequence} '{args.actionID}'): currently {_pendingActions.Count} are pending", true);
                _pendingActions.Clear();
            }
            else
            {
                if (index > 0)
                {
                    Log($"Unexpected action-reject ({PendingActionString(_pendingActions[index])}): index={index}, first={PendingActionString(_pendingActions[0])}, count={_pendingActions.Count}", true);
                    _pendingActions.RemoveRange(0, index);
                }
                if (_pendingActions[0].Action.ID != args.actionID)
                {
                    Log($"Request/reject action mismatch: requested {PendingActionString(_pendingActions[0])}, got {args.actionID}", true);
                }
                Log($"!! {PendingActionString(_pendingActions[0])}");
                _pendingActions.RemoveAt(0);
            }
        }

        private string PendingActionString(Network.PendingAction a)
        {
            return $"#{a.Sequence} {a.Action} @ {Utils.ObjectString(a.TargetID)}";
        }

        private void Log(string message, bool warning = false)
        {
            if (warning || _config.AutorotationLogging)
                Service.Log($"[AR] {message}");
        }

        private ulong GetAdjustedActionIdDetour(byte self, uint actionID)
        {
            if (Service.ClientState.LocalPlayer == null)
                return _getAdjustedActionIdHook.Original(self, actionID);

            switch (actionID)
            {
                case (uint)WARRotation.AID.HeavySwing:
                    return (uint)WarActions.NextBestAction;
                case (uint)WARRotation.AID.StormEye:
                    return (uint)WARRotation.GetNextStormEyeComboAction(WarActions.State);
                case (uint)WARRotation.AID.StormPath:
                    return (uint)WARRotation.GetNextStormPathComboAction(WarActions.State);
                case (uint)WARRotation.AID.MythrilTempest:
                    return (uint)WARRotation.GetNextAOEComboAction(WarActions.State);
                default:
                    return _getAdjustedActionIdHook.Original(self, actionID);
            }
        }
    }
}
