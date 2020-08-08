#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.RV.Traits.BotModules.Squads
{
	abstract class AirStateBaseRV : StateBaseRV
	{
		static readonly BitSet<TargetableType> AirTargetTypes = new BitSet<TargetableType>("Air");

		protected static int CountAntiAirUnits(IEnumerable<Actor> units)
		{
			if (!units.Any())
				return 0;

			var missileUnitsCount = 0;
			foreach (var unit in units)
			{
				if (unit == null)
					continue;

				foreach (var ab in unit.TraitsImplementing<AttackBase>())
				{
					if (ab.IsTraitDisabled || ab.IsTraitPaused)
						continue;

					foreach (var a in ab.Armaments)
					{
						if (a.Weapon.IsValidTarget(AirTargetTypes))
						{
							if (unit.Info.HasTraitInfo<AircraftInfo>())
								missileUnitsCount += 1;
							else
								missileUnitsCount += 3;
							break;
						}
					}
				}
			}

			return missileUnitsCount;
		}

		protected static Actor FindDefenselessTarget(SquadRV owner)
		{
			Actor target = null;
			FindSafePlace(owner, out target, true);

			if (target != null)
				foreach (var a in owner.Units)
					if (CanAttackTarget(a, target))
						return target;

			return null;
		}

		protected static CPos? FindSafePlace(SquadRV owner, out Actor detectedEnemyTarget, bool needTarget)
		{
			var map = owner.World.Map;
			var dangerRadius = owner.SquadManager.Info.DangerScanRadius;
			detectedEnemyTarget = null;

			var columnCount = (map.MapSize.X + dangerRadius - 1) / dangerRadius;
			var rowCount = (map.MapSize.Y + dangerRadius - 1) / dangerRadius;
			var checkIndices = Exts.MakeArray(columnCount * rowCount, i => i).Shuffle(owner.World.LocalRandom);
			foreach (var i in checkIndices)
			{
				var pos = new CPos((i % columnCount) * dangerRadius, (i / columnCount) * dangerRadius);
				if (NearToPosSafely(owner, map.CenterOfCell(pos), out detectedEnemyTarget))
				{
					if (needTarget && detectedEnemyTarget == null)
						continue;

					return pos;
				}
			}

			return null;
		}

		protected static bool NearToPosSafely(SquadRV owner, WPos loc)
		{
			Actor a;
			return NearToPosSafely(owner, loc, out a);
		}

		protected static bool NearToPosSafely(SquadRV owner, WPos loc, out Actor detectedEnemyTarget)
		{
			detectedEnemyTarget = null;
			var dangerRadius = owner.SquadManager.Info.DangerScanRadius;
			var unitsAroundPos = owner.World.FindActorsInCircle(loc, WDist.FromCells(dangerRadius))
				.Where(a => owner.SquadManager.IsPreferredEnemyUnit(a) && owner.SquadManager.IsNotHiddenUnit(a)).ToList();

			if (!unitsAroundPos.Any())
				return true;

			if (CountAntiAirUnits(unitsAroundPos) < owner.Units.Count)
			{
				detectedEnemyTarget = unitsAroundPos.Random(owner.Random);
				return true;
			}

			return false;
		}

		// Checks the number of anti air enemies around units
		protected virtual bool ShouldFlee(SquadRV owner)
		{
			return ShouldFlee(owner, enemies => CountAntiAirUnits(enemies) > owner.Units.Count);
		}
	}

	class AirIdleStateRV : AirStateBaseRV, IState
	{
		public void Activate(SquadRV owner) { }

		public void Tick(SquadRV owner)
		{
			if (!owner.IsValid)
				return;

			if (ShouldFlee(owner))
			{
				owner.FuzzyStateMachine.ChangeState(owner, new AirFleeStateRV(), true);
				return;
			}

			var e = FindDefenselessTarget(owner);
			if (e == null)
			{
				Retreat(owner, false, true, true);
				return;
			}

			owner.TargetActor = e;
			owner.FuzzyStateMachine.ChangeState(owner, new AirAttackStateRV(), true);
		}

		public void Deactivate(SquadRV owner) { }
	}

	class AirAttackStateRV : AirStateBaseRV, IState
	{
		public void Activate(SquadRV owner) { }

		public void Tick(SquadRV owner)
		{
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				var a = owner.Units.Random(owner.Random);
				var closestEnemy = owner.SquadManager.FindClosestEnemy(a.CenterPosition);
				if (closestEnemy != null)
					owner.TargetActor = closestEnemy;
				else
				{
					owner.FuzzyStateMachine.ChangeState(owner, new AirFleeStateRV(), true);
					return;
				}
			}

			var teamLeader = owner.Units.ClosestTo(owner.TargetActor.CenterPosition);

			var unitsAroundPos = owner.World.FindActorsInCircle(teamLeader.CenterPosition, WDist.FromCells(owner.SquadManager.Info.DangerScanRadius))
				.Where(a => owner.SquadManager.IsPreferredEnemyUnit(a) && owner.SquadManager.IsNotHiddenUnit(a));
			var ambushed = CountAntiAirUnits(unitsAroundPos) > owner.Units.Count;

			if ((!NearToPosSafely(owner, owner.TargetActor.CenterPosition)) || ambushed)
			{
				owner.FuzzyStateMachine.ChangeState(owner, new AirFleeStateRV(), true);
				return;
			}

			foreach (var a in owner.Units)
			{
				if (BusyAttack(a))
					continue;

				var ammoPools = a.TraitsImplementing<AmmoPool>().ToArray();
				if (!ReloadsAutomatically(ammoPools, a.TraitOrDefault<Rearmable>()))
				{
					if (IsRearming(a))
						continue;

					if (!HasAmmo(ammoPools))
					{
						owner.Bot.QueueOrder(new Order("ReturnToBase", a, false));
						continue;
					}
				}

				if (CanAttackTarget(a, owner.TargetActor))
					owner.Bot.QueueOrder(new Order("Attack", a, Target.FromActor(owner.TargetActor), false));
				else
					owner.Bot.QueueOrder(new Order("Move", a, Target.FromCell(owner.World, RandomBuildingLocation(owner)), false));
			}
		}

		public void Deactivate(SquadRV owner) { }
	}

	class AirFleeStateRV : AirStateBaseRV, IState
	{
		public void Activate(SquadRV owner) { }

		public void Tick(SquadRV owner)
		{
			if (!owner.IsValid)
				return;

			Retreat(owner, true, true, true);

			owner.FuzzyStateMachine.ChangeState(owner, new AirIdleStateRV(), true);
		}

		public void Deactivate(SquadRV owner) { }
	}
}
