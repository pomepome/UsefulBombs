using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Harmony;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.TerrainFeatures;
using Object = StardewValley.Object;

namespace UsefulBombs
{
    internal class ModEntry : Mod
    {
        internal static IReflectionHelper Reflection { get; private set; }
        internal static ModConfig Config { get; private set; }
        internal static IMonitor Mon { get; private set; }

        public override void Entry(IModHelper helper)
        {
            Reflection = helper.Reflection;
            Config = helper.ReadConfig<ModConfig>();
            Mon = Monitor;

            Cap(ref Config.DamageMultiplier, 1, 3);
            Cap(ref Config.RadiusIncreaseRatio, 0.1f, 0.5f);
            Helper.WriteConfig(Config);

            HarmonyInstance harmony = HarmonyInstance.Create("punyo.usefulbombs");
            MethodInfo methodBase = typeof(GameLocation).GetMethod("explode", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo methodPatcher = typeof(GameLocationPatcher).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            if (methodBase == null)
            {
                Monitor.Log("Original method null, what's wrong?");
                return;
            }
            if (methodPatcher == null)
            {
                Monitor.Log("Patcher null, what's wrong?");
                return;
            }
            harmony.Patch(methodBase, new HarmonyMethod(methodPatcher), null);
            Monitor.Log($"Patched {methodBase.DeclaringType?.FullName}.{methodBase.Name} by {methodPatcher.DeclaringType?.FullName}.{methodPatcher.Name}");
        }

        private static void Cap(ref float f, float min, float max)
        {
            if (f < min)
            {
                f = min;
            }
            else if (f > max)
            {
                f = max;
            }
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    internal class GameLocationPatcher
    {
        internal static bool Prefix(ref GameLocation __instance, ref Vector2 tileLocation, ref int radius,  ref Farmer who)
        {
            return Explode(__instance, ModEntry.Config, tileLocation, radius, who);
        }

        internal static bool IsCrystals(Object obj)
        {
            return (obj.ParentSheetIndex >= 80 && obj.ParentSheetIndex <= 86) || obj.ParentSheetIndex == 420 || obj.ParentSheetIndex == 422;
        }

        internal static bool Explode(GameLocation location, ModConfig config, Vector2 tileLocation, int radius, Farmer who)
        {
            if (!(location is MineShaft))
            {
                // Call original method when exploded outside of mines.
                return true;
            }
            if (config.LargerRadius)
            {
                int oRadius = radius;
                radius = (int) (radius * (1 + config.RadiusIncreaseRatio));
                ModEntry.Mon.Log($"Radius changed from {oRadius} to {radius}", LogLevel.Trace);
            }
            Rectangle areaOfEffect = new Rectangle((int)(tileLocation.X - radius - 1f) * 64, (int)(tileLocation.Y - radius - 1f) * 64, (radius * 2 + 1) * 64, (radius * 2 + 1) * 64);

            float minDamage = radius * 6, maxDamage = radius * 8;
            if (config.ModifyDamagesToEnemies)
            {
                minDamage *= config.DamageMultiplier;
                maxDamage *= config.DamageMultiplier;
            }

            foreach (Monster monster in location.characters.OfType<Monster>().Where(m => m.GetBoundingBox().Intersects(areaOfEffect)))
            {
                if (config.CancelEnemyInvincibility)
                {
                    switch (monster)
                    {
                        case Bug bug:
                            bug.isArmoredBug.Value = false;
                            break;
                        case Grub grub:
                            grub.hard.Value = false;
                            break;
                    }
                }

                if (config.InstantKillMummies && monster is Mummy mummy && (Game1.random.NextDouble() < 0.6f + Game1.dailyLuck || mummy.Health <= minDamage))
                {
                    ModEntry.Mon.Log("Instant Killed Mummy");
                    ModEntry.Reflection.GetField<int>(mummy, "reviveTimer").SetValue(10000);
                }
            }

            IReflectedMethod rumbleAndFade = ModEntry.Reflection.GetMethod(location, "rumbleAndFade");
            IReflectedMethod damagePlayers = ModEntry.Reflection.GetMethod(location, "damagePlayers");
            Multiplayer multiplayer = ModEntry.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();

            bool insideCircle = false;
            location.updateMap();
            Vector2 currentTile = new Vector2(Math.Min(location.Map.Layers[0].LayerWidth - 1, Math.Max(0f, tileLocation.X - radius)), Math.Min(location.Map.Layers[0].LayerHeight - 1, Math.Max(0f, tileLocation.Y - radius)));
            bool[,] circleOutline2 = Game1.getCircleOutlineGrid(radius);
            location.damageMonster(areaOfEffect, (int)minDamage, (int)maxDamage, true, who);
            /*List<TemporaryAnimatedSprite> sprites = new List<TemporaryAnimatedSprite>
            {
                new TemporaryAnimatedSprite(23, 9999f, 6, 1, new Vector2(currentTile.X * 64f, currentTile.Y * 64f),
                    false, Game1.random.NextDouble() < 0.5)
                {
                    light = true,
                    lightRadius = radius,
                    lightcolor = Color.Black,
                    alphaFade = 0.03f - radius * 0.003f
                }
            };*/
            var sprites = new List<TemporaryAnimatedSprite>();
            rumbleAndFade.Invoke(300 + radius * 100);
            damagePlayers.Invoke(areaOfEffect, radius * 3);
            for (int n = location.terrainFeatures.Count() - 1; n >= 0; n--)
            {
                KeyValuePair<Vector2, TerrainFeature> m = location.terrainFeatures.Pairs.ElementAt(n);
                if (m.Value.getBoundingBox(m.Key).Intersects(areaOfEffect) && m.Value.performToolAction(null, radius / 2, m.Key, location))
                {
                    location.terrainFeatures.Remove(m.Key);
                }
            }
            if (config.BreakBoulders && location is MineShaft shaft)
            {
                for (int num = shaft.resourceClumps.Count - 1; num >= 0; num--)
                {
                    ResourceClump terrain = shaft.resourceClumps[num];
                    switch (terrain.parentSheetIndex.Value)
                    {
                        case 672:
                        case 752:
                        case 754:
                        case 756:
                        case 758: break;
                        default: continue;
                    }
                    Vector2 vec = terrain.tile.Value;
                    if (terrain.getBoundingBox(vec).Intersects(areaOfEffect))
                    {
                        int number = (terrain.parentSheetIndex.Value == 672) ? 15 : 10;
                        if (Game1.IsMultiplayer)
                        {
                            Game1.createMultipleObjectDebris(390, (int)vec.X, (int)vec.Y, number, Game1.player.UniqueMultiplayerID);
                        }
                        else
                        {
                            Game1.createRadialDebris(Game1.currentLocation, 390, (int)vec.X, (int)vec.Y, number, false, -1, true);
                        }
                        location.playSound("boulderBreak");
                        Game1.createRadialDebris(Game1.currentLocation, 32, (int)vec.X, (int)vec.Y, Game1.random.Next(6, 12), false);
                        shaft.resourceClumps.RemoveAt(num);
                    }
                }
            }
            for (int l = 0; l < radius * 2 + 1; l++)
            {
                for (int i = 0; i < radius * 2 + 1; i++)
                {
                    if (l == 0 || i == 0 || l == radius * 2 || i == radius * 2)
                    {
                        insideCircle = circleOutline2[l, i];
                    }
                    else if (circleOutline2[l, i])
                    {
                        insideCircle = !insideCircle;
                        if (!insideCircle)
                        {
                            if (location.Objects.ContainsKey(currentTile) && location.Objects[currentTile].onExplosion(who, location))
                            {
                                if (config.CollectCrystals && location.Objects.ContainsKey(currentTile) && IsCrystals(location.Objects[currentTile]))
                                {
                                    Game1.createObjectDebris(location.Objects[currentTile].ParentSheetIndex, (int)currentTile.X, (int)currentTile.Y);
                                }
                                location.destroyObject(currentTile, who);
                            }
                            if (Game1.random.NextDouble() < 0.45)
                            {
                                if (Game1.random.NextDouble() < 0.5)
                                {
                                    sprites.Add(new TemporaryAnimatedSprite(362, Game1.random.Next(30, 90), 6, 1, new Vector2(currentTile.X * 64f, currentTile.Y * 64f), false, Game1.random.NextDouble() < 0.5)
                                    {
                                        delayBeforeAnimationStart = Game1.random.Next(700)
                                    });
                                }
                                else
                                {
                                    sprites.Add(new TemporaryAnimatedSprite(5, new Vector2(currentTile.X * 64f, currentTile.Y * 64f), Color.White, 8, false, 50f)
                                    {
                                        delayBeforeAnimationStart = Game1.random.Next(200),
                                        scale = Game1.random.Next(5, 15) / 10f
                                    });
                                }
                            }
                        }
                    }
                    if (insideCircle)
                    {
                        if (location.Objects.ContainsKey(currentTile) && location.Objects[currentTile].onExplosion(who, location))
                        {
                            if (config.CollectCrystals && location.Objects.ContainsKey(currentTile) && IsCrystals(location.Objects[currentTile]))
                            {
                                Game1.createObjectDebris(location.Objects[currentTile].ParentSheetIndex, (int)currentTile.X, (int)currentTile.Y);
                            }
                            location.destroyObject(currentTile, who);
                        }
                        if (Game1.random.NextDouble() < 0.45)
                        {
                            if (Game1.random.NextDouble() < 0.5)
                            {
                                sprites.Add(new TemporaryAnimatedSprite(362, Game1.random.Next(30, 90), 6, 1, new Vector2(currentTile.X * 64f, currentTile.Y * 64f), false, Game1.random.NextDouble() < 0.5)
                                {
                                    delayBeforeAnimationStart = Game1.random.Next(700)
                                });
                            }
                            else
                            {
                                sprites.Add(new TemporaryAnimatedSprite(5, new Vector2(currentTile.X * 64f, currentTile.Y * 64f), Color.White, 8, false, 50f)
                                {
                                    delayBeforeAnimationStart = Game1.random.Next(200),
                                    scale = Game1.random.Next(5, 15) / 10f
                                });
                            }
                        }
                        sprites.Add(new TemporaryAnimatedSprite(6, new Vector2(currentTile.X * 64f, currentTile.Y * 64f), Color.White, 8, Game1.random.NextDouble() < 0.5, Vector2.Distance(currentTile, tileLocation) * 20f));
                    }
                    currentTile.Y += 1f;
                    currentTile.Y = Math.Min(location.Map.Layers[0].LayerHeight - 1, Math.Max(0f, currentTile.Y));
                }
                currentTile.X += 1f;
                currentTile.Y = Math.Min(location.Map.Layers[0].LayerWidth - 1, Math.Max(0f, currentTile.X));
                currentTile.Y = tileLocation.Y - radius;
                currentTile.Y = Math.Min(location.Map.Layers[0].LayerHeight - 1, Math.Max(0f, currentTile.Y));
            }
            multiplayer.broadcastSprites(location, sprites);
            radius /= 2;
            circleOutline2 = Game1.getCircleOutlineGrid(radius);
            currentTile = new Vector2((int)(tileLocation.X - radius), (int)(tileLocation.Y - radius));
            for (int k = 0; k < radius * 2 + 1; k++)
            {
                for (int j = 0; j < radius * 2 + 1; j++)
                {
                    if (k == 0 || j == 0 || k == radius * 2 || j == radius * 2)
                    {
                        insideCircle = circleOutline2[k, j];
                    }
                    else if (circleOutline2[k, j])
                    {
                        insideCircle = !insideCircle;
                        if (!insideCircle && !location.Objects.ContainsKey(currentTile) && Game1.random.NextDouble() < 0.9 && location.doesTileHaveProperty((int)currentTile.X, (int)currentTile.Y, "Diggable", "Back") != null && !location.isTileHoeDirt(currentTile))
                        {
                            location.checkForBuriedItem((int)currentTile.X, (int)currentTile.Y, true, false);
                            location.makeHoeDirt(currentTile);
                        }
                    }
                    if (insideCircle && !location.Objects.ContainsKey(currentTile) && Game1.random.NextDouble() < 0.9 && location.doesTileHaveProperty((int)currentTile.X, (int)currentTile.Y, "Diggable", "Back") != null && !location.isTileHoeDirt(currentTile))
                    {
                        location.checkForBuriedItem((int)currentTile.X, (int)currentTile.Y, true, false);
                        location.makeHoeDirt(currentTile);
                    }
                    currentTile.Y += 1f;
                    currentTile.Y = Math.Min(location.Map.Layers[0].LayerHeight - 1, Math.Max(0f, currentTile.Y));
                }
                currentTile.X += 1f;
                currentTile.Y = Math.Min(location.Map.Layers[0].LayerWidth - 1, Math.Max(0f, currentTile.X));
                currentTile.Y = tileLocation.Y - radius;
                currentTile.Y = Math.Min(location.Map.Layers[0].LayerHeight - 1, Math.Max(0f, currentTile.Y));
            }
            return false;
        }
    }
}
