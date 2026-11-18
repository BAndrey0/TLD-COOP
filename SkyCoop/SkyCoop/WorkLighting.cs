using HarmonyLib;
using Il2Cpp;
using UnityEngine;
namespace SkyCoop;
public static class WorkLighting
{
 private static TimeOfDay owner;private static float scale,deadline;
 public static void Begin(TimeOfDay tod,float duration)
 {
  if(!MyMod.InOnline()||tod==null||GameManager.m_Rest?.IsSleeping()==true)return;
  if(owner==null){owner=tod;scale=VitalNumbers.Rate(tod.GetDayLengthScale(),1);}
  deadline=Time.realtimeSinceStartup+VitalNumbers.Rate(duration,30)+10;
 }
 public static void End(TimeOfDay tod)
 {
  if(tod==null||owner==null||tod.Pointer!=owner.Pointer||tod.IsTimeLapseActive())return;
  Restore(tod);
 }

 internal static (TimeOfDay Clock, float Scale) UseNormalClock()
 {
  var clock = owner;
  if (clock == null || GameManager.m_Rest?.IsSleeping() == true) return default;
  float accelerated = clock.GetDayLengthScale();
  if (accelerated == scale) return default;
  clock.SetDayLengthScale(scale);
  return (clock, accelerated);
 }

 internal static void RestoreWorkClock((TimeOfDay Clock, float Scale) state)
 {
  if (state.Clock != null) state.Clock.SetDayLengthScale(state.Scale);
 }

 [HarmonyPatch]
 private static class WorldClockUpdate
 {
  private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
  {
   yield return AccessTools.Method(typeof(TimeOfDay), nameof(TimeOfDay.Update));
   yield return AccessTools.Method(typeof(WeatherTransition), nameof(WeatherTransition.Update));
  }
  private static void Prefix(out (TimeOfDay Clock, float Scale) __state) => __state = UseNormalClock();
  private static void Finalizer((TimeOfDay Clock, float Scale) __state) => RestoreWorkClock(__state);
 }
 private static void Restore(TimeOfDay tod)
 {
  owner=null;tod.SetDayLengthScale(scale);
  if(Utils.TryParseTOD(MyMod.OveridedTime,out var time))tod.SetNormalizedTime(time,true);
  tod.ForceUpdateTodObjects();tod.SetCameraVignettingIndoors();
 }
 public static void SyncSky()
 {
  if (!MyMod.InOnline() || MyMod.LoadingScreenIsOn || GameManager.m_Rest?.IsSleeping() == true) return;
  var clock = GameManager.GetTimeOfDayComponent();
  if (clock != null && clock.IsTimeLapseActive() && Utils.TryParseTOD(MyMod.OveridedTime, out var time))
      GameManager.GetUniStorm()?.SetNormalizedTime(time, false);
 }

 public static void Tick()
 {
  if(owner==null)return;
  if(!MyMod.InOnline()||MyMod.LoadingScreenIsOn){owner=null;return;}
  if(!owner.IsTimeLapseActive()){Restore(owner);return;}
  if(Time.realtimeSinceStartup>deadline&&!(GameManager.m_Rest?.IsSleeping()??false))
  {var tod=owner;tod.m_IsTimeLapseActiveCounter=0;Restore(tod);}
 }
}
