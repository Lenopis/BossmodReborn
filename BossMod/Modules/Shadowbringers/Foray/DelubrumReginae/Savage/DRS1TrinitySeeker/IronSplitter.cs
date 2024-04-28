﻿namespace BossMod.Shadowbringers.Foray.DelubrumReginae.Savage.DRS1TrinitySeeker;

class IronSplitter(BossModule module) : Components.GenericAOEs(module, ActionID.MakeSpell(AID.IronSplitter))
{
    private List<AOEInstance> _aoes = new();

    public override IEnumerable<AOEInstance> ActiveAOEs(int slot, Actor actor) => _aoes;

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action == WatchedAction)
        {
            var distance = (caster.Position - Module.Bounds.Center).Length();
            if (distance is <3 or >9 and <11 or >17 and <19) // tiles
            {
                _aoes.Add(new(new AOEShapeCircle(4), Module.Bounds.Center, new(), spell.NPCFinishAt));
                _aoes.Add(new(new AOEShapeDonut(8, 12), Module.Bounds.Center, new(), spell.NPCFinishAt));
                _aoes.Add(new(new AOEShapeDonut(16, 20), Module.Bounds.Center, new(), spell.NPCFinishAt));
            }
            else
            {
                _aoes.Add(new(new AOEShapeDonut(4, 8), Module.Bounds.Center, new(), spell.NPCFinishAt));
                _aoes.Add(new(new AOEShapeDonut(12, 16), Module.Bounds.Center, new(), spell.NPCFinishAt));
                _aoes.Add(new(new AOEShapeDonut(20, 25), Module.Bounds.Center, new(), spell.NPCFinishAt));
            }
        }
    }

    public override void OnCastFinished(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action == WatchedAction)
        {
            _aoes.Clear();
        }
    }
}