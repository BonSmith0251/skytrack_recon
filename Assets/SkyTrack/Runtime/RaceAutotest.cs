using System.Collections;
using System.Linq;
using UnityEngine;

namespace SkyTrack
{
    /// <summary>
    /// Play-mode self test (player started with -skytrackAutotest): checks the grid on the first frames,
    /// drives the player's car, presses R mid-race and throws the car off the track, and logs whether every
    /// car is back on the race line and still making progress, then drives to the finish and checks the final
    /// position holds (HUD screenshots in autotest/ beside the player data). Quits when done.
    /// </summary>
    public class RaceAutotest : MonoBehaviour
    {
        RaceDirector director;

        IEnumerator Start()
        {
            director = GetComponent<RaceDirector>();
            yield return new WaitForFixedUpdate();
            yield return null;
            Log("grid", director.drivers.Select(d => GridError(d)).Max().ToString("0.00") + " max distance from grid slot (frame 1)");
            var p = director.Player;
            p.Car.useKeyboard = false;
            while (!director.Racing) yield return null;
            yield return new WaitForSeconds(15f);
            float before = p.progress;
            p.Car.respawnRequested = true;                                  // the R key
            yield return new WaitForSeconds(0.5f);
            Log("R respawn", Report(p) + $" progress {before:0}->{p.progress:0}");
            yield return new WaitForSeconds(8f);
            Log("after R +8s", Report(p));
            p.Car.Teleport(p.transform.position + p.transform.right * 70f - Vector3.up * 30f, p.transform.rotation);   // off the track
            float t0 = Time.time, prog = p.progress;
            yield return new WaitForSeconds(6f);
            Log("off-track recovery", Report(p) + $" respawns {p.respawns}");
            yield return new WaitForSeconds(8f);
            Log("after recovery +8s", Report(p) + $" progress {prog:0}->{p.progress:0}");
            Log("standings", string.Join(", ", director.drivers.OrderBy(d => d.position).Select(d => $"{d.position}.{d.driverName} {d.progress:0}/{d.respawns}r")));
            Shot("hud_racing");
            p.isPlayer = false;                                             // the AI drives the player's car to the line
            float limit = Time.time + 240f;
            while (!p.finished && Time.time < limit) yield return null;
            if (p.finished) Shot("finish_line");
            Log("finish", p.finished ? $"player {p.finalPosition} of {director.drivers.Count} in {p.finishTime:0.0}s at {p.progress:0} of {director.Path.Length:0} (line {director.FinishDistance:0})" : "player did not finish in 240 s");
            int fixedPos = p.position;
            yield return new WaitForSeconds(4f);
            Log("final position held", $"{fixedPos} -> {p.position}, state {director.state}");
            Shot("hud_finished");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        void FixedUpdate()
        {
            var p = director ? director.Player : null;
            if (p && p.isPlayer && p.path != null && director.Racing) p.Car.SetInput(1f, p.AutoSteer(20f));
        }

        float GridError(RaceDriver d)
        {
            var path = director.Path;
            float best = float.MaxValue;
            int total = director.drivers.Count, rows = (total + 2) / 3;
            for (int i = 0; i < total; i++)
            {
                var pose = path.PoseAt(director.gridStart + director.rowSpacing * (rows - 1 - i / 3), (i % 3 - 1) * director.laneSpacing, 1.2f);
                best = Mathf.Min(best, Vector3.Distance(pose.position, d.transform.position));
            }
            return best;
        }

        string Report(RaceDriver d)
        {
            var path = director.Path;
            int i = path.Nearest(d.transform.position, path.IndexAt(d.progress), 20, 20);
            Vector3 rel = d.transform.position - path.points[i];
            return $"{d.driverName}: off line {Vector3.Dot(rel, path.right[i]):0.0} lateral / {Vector3.Dot(rel, path.up[i]):0.0} up, " +
                   $"upright {Vector3.Dot(d.transform.up, path.up[i]):0.00}, facing {Vector3.Dot(d.transform.forward, path.forward[i]):0.00}, {d.Car.SpeedKmh:0} km/h";
        }

        static void Shot(string name)
        {
            var dir = System.IO.Path.Combine(Application.dataPath, "..", "autotest");
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
        }

        static void Log(string what, string msg) => Debug.Log($"[SkyTrack] autotest {what}: {msg}");
    }
}
