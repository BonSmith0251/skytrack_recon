using System.Collections.Generic;
using UnityEngine;

namespace SkyTrack
{
    /// <summary>Everything a race course is built from: the 19 platform types (generator order) and the scenery prefabs.</summary>
    public class TrackLibrary : ScriptableObject
    {
        public List<TrackShape> shapes = new List<TrackShape>();     // index = generator type (0..18)
        public GameObject start, gate, infoLeft, infoRight, boxes, signs;
        public GameObject[] towerMain = new GameObject[0];          // Structures/Main01..03
        public GameObject[] towerUp = new GameObject[0];            // Structures/Up01..02
        [Tooltip("Platform materials for texture variant 1 (Main01) and their variant 2 (Main02) replacements")]
        public Material[] platformVariant1 = new Material[0];
        public Material[] platformVariant2 = new Material[0];
        public Material[] boxMaterials = new Material[0];           // Boxes/Main01_01..06
        [Tooltip("Mesh bounds of the decor templates (the decor pass stacks and drops by these)")]
        public TrackScenery.DecorTemplates decorTemplates;
        [Header("Racers")]
        public GameObject car;                                      // assembled drivable car (opponents use it too)
        public VehicleStats statsMin, statsMax;                     // Main_Min.obj / Main_Max.obj
        public string[] menFirst = new string[0], menLast = new string[0], womenFirst = new string[0], womenLast = new string[0];
    }
}
