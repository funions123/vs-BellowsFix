using HarmonyLib;
using System.Collections;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;

namespace BellowsFix
{
    public sealed class BellowsFixModSystem : ModSystem
    {
        private Harmony? _patcher;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            _patcher ??= new Harmony(Mod.Info.ModID);
            _patcher.PatchAll(typeof(BellowsFixModSystem).Assembly);
        }

        public override void Dispose()
        {
            _patcher?.UnpatchAll(Mod.Info.ModID);
            base.Dispose();
        }
    }

    // Bug: OnTesselation used -HorizontalAngleIndex*90 for the animator, which is 180° off the
    // static shape for N/S facings. Fixed to match BlockEntityBellows: HorizontalAngleIndex*90+180.
    [HarmonyPatch(typeof(BlockEntityMechPoweredBellows), nameof(BlockEntityMechPoweredBellows.OnTesselation))]
    internal static class LargeBellowsRotationPatch
    {
        public static bool Prefix(BlockEntityMechPoweredBellows __instance, ref bool __result)
        {
            var animUtil = Traverse.Create(__instance).Field<BlockEntityAnimationUtil>("animUtil").Value;

            if (animUtil?.animator == null)
            {
                var facing = BlockFacing.FromCode(__instance.Block.Variant["side"]);
                var rot = new Vec3f(0, facing.HorizontalAngleIndex * 90 + 180, 0);
                animUtil?.InitializeAnimator("mechpoweredbellows", null, null, rot);
            }

            var activeAnimations = Traverse.Create(animUtil).Field("activeAnimationsByAnimCode").GetValue() as IDictionary;
            __result = (activeAnimations?.Count > 0) ||
                       (animUtil?.animator != null && animUtil.animator.ActiveAnimationCount > 0);
            return false;
        }
    }

    // Bug: onTick never called BlowAirInto, so the large bellows had no functional effect.
    // Added: BlowAirInto on both sides (server for heating, client for forge particles per
    // BlockEntityBellows.Interact's two-sided pattern) at each half-rotation. Stroke timing uses
    // an angular accumulator (angle delta → fire every π) based on BEHelveHammer's accumHits
    // pattern, which is robust at any speed and fires at exact half/full rotation positions.
    [HarmonyPatch(typeof(BlockEntityMechPoweredBellows), "onTick")]
    internal static class LargeBellowsAirPatch
    {
        private static readonly ConditionalWeakTable<BlockEntityMechPoweredBellows, PumpState> _states = new();
        private sealed class PumpState { public float Accum; public int StrokeCount; public float PrevAngle = float.NaN; }

        public static void Postfix(BlockEntityMechPoweredBellows __instance)
        {
            var connected = Traverse.Create(__instance).Field<bool>("connected").Value;
            var mpc = Traverse.Create(__instance).Field<BEBehaviorMPConsumer>("mpc").Value;

            if (!connected || mpc?.Network == null || mpc.Network.Speed <= 0f)
            {
                if (_states.TryGetValue(__instance, out var s)) s.PrevAngle = float.NaN;
                return;
            }

            var state = _states.GetOrCreateValue(__instance);
            float angle = mpc.AngleRad;

            if (!float.IsNaN(state.PrevAngle))
            {
                float delta = angle - state.PrevAngle;
                if (delta < 0) delta += GameMath.TWOPI;
                state.Accum += delta;

                if (state.Accum >= GameMath.PI)
                {
                    var facing    = BlockFacing.FromCode(__instance.Block.Variant["side"]);
                    var facingPos = __instance.Pos.AddCopy(facing);
                    var block     = __instance.Api.World.BlockAccessor.GetBlock(facingPos);
                    var receiver  = block.GetInterface<IBellowsAirReceiver>(__instance.Api.World, facingPos);
                    bool server   = __instance.Api.Side == EnumAppSide.Server;

                    while (state.Accum >= GameMath.PI)
                    {
                        state.Accum -= GameMath.PI;
                        state.StrokeCount++;

                        receiver?.BlowAirInto(__instance.Api.World, facingPos, 0.2f, facing);

                        if (server)
                            PlayBellowsSound(__instance, state.StrokeCount % 2 == 1 ? "in" : "out");
                    }
                }
            }

            state.PrevAngle = angle;
        }

        private static void PlayBellowsSound(BlockEntityMechPoweredBellows be, string type)
        {
            int variant = be.Api.World.Rand.Next(1, 4);
            be.Api.World.PlaySoundAt(
                new AssetLocation($"game:sounds/block/bellowslarge/bellowlarge-{type}{variant}"),
                be.Pos, 1.0, null, true, 15f);
        }
    }
}
