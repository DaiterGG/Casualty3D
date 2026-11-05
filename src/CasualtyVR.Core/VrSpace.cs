using UnityEngine;

namespace CasualtyVR
{
    /// <summary>
    /// The one mapping from tracking space (OpenXR LOCAL, converted to Unity axes, metres) to the game world,
    /// shared by the eye cameras, the controllers, the laser, the hands and the UI panels, so they always agree.
    ///
    /// * Seat: the seated origin and heading (yaw) in tracking space. The heading is the runtime's LOCAL forward
    ///   unless the player recentred (F9) facing elsewhere; "in front of the player" means along the heading.
    /// * Diorama: the game plane is DioramaDistance metres in front of the seat, UnitsPerMetre world units per
    ///   metre (world +z = away from the player along the heading).
    /// * First person (FirstPerson.cs): the seated origin is the character's eye, the player's forward (heading)
    ///   is the character's facing along world x (yaw ±90°), FirstPerson.Scale units per metre.
    /// </summary>
    internal static class VrSpace
    {
        public static bool FirstPerson { get; private set; }
        /// <summary>World units per metre.</summary>
        public static float Scale { get; private set; } = 25f;
        /// <summary>Tracking-to-world rotation.</summary>
        public static Quaternion Rotation { get; private set; } = Quaternion.identity;

        private static Vector3 trackOrigin, worldOrigin;

        /// <summary>The seat's heading in tracking space (yaw only).</summary>
        public static Quaternion Heading { get; private set; } = Quaternion.identity;

        public static void SetHeading(Quaternion heading) => Heading = heading;

        /// <summary>A point given relative to the seat (x right, y up, z forward along the heading), in tracking space.</summary>
        public static Vector3 Seated(Vector3 seatedOrigin, Vector3 local) => seatedOrigin + Heading * local;
        /// <summary>A rotation given relative to the seat's heading, in tracking space.</summary>
        public static Quaternion SeatedRot(Quaternion local) => Heading * local;

        /// <summary>Yaw-only rotation of a tracking-space orientation (its forward projected onto the floor).</summary>
        public static Quaternion YawOf(Quaternion q)
        {
            Vector3 f = q * Vector3.forward;
            f.y = 0f;
            return f.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(f.normalized, Vector3.up);
        }

        public static void SetDiorama(Vector3 focus, Vector3 seatedOrigin)
        {
            FirstPerson = false;
            Scale = Plugin.UnitsPerMetre.Value;
            Rotation = Quaternion.Inverse(Heading);
            trackOrigin = Seated(seatedOrigin, new Vector3(0f, 0f, Plugin.DioramaDistance.Value));
            worldOrigin = focus;
        }

        public static void SetFirstPerson(Vector3 eyeWorld, Vector3 seatedOrigin, float yawDegrees, float scale)
        {
            FirstPerson = true;
            Scale = scale;
            Rotation = Quaternion.Euler(0f, yawDegrees, 0f) * Quaternion.Inverse(Heading);
            trackOrigin = seatedOrigin;
            worldOrigin = eyeWorld;
        }

        /// <summary>The player's head (centre eye) in world space, this frame.</summary>
        public static Vector3 HeadPosition { get; private set; }
        public static Vector3 HeadForward { get; private set; } = Vector3.forward;

        /// <summary>The head (centre eye) in tracking space (Unity axes, metres): where panels open in front of you.</summary>
        public static Vector3 HeadTrackingPos { get; private set; }
        public static Quaternion HeadTrackingRot { get; private set; } = Quaternion.identity;

        public static void SetHead(Vector3 trackingPos, Quaternion trackingRot)
        {
            HeadTrackingPos = trackingPos;
            HeadTrackingRot = trackingRot;
            HeadPosition = Point(trackingPos);
            HeadForward = Rot(trackingRot) * Vector3.forward;
        }

        public static void SetWorldHead(Vector3 position, Quaternion rotation)
        {
            HeadTrackingPos = ToTracking(position);
            HeadTrackingRot = Quaternion.Inverse(Rotation) * rotation;
            HeadPosition = position;
            HeadForward = rotation * Vector3.forward;
        }

        /// <summary>First person: the character's (smoothed) eye in world space, which the seated origin maps to.</summary>
        public static Vector3 Anchor => worldOrigin;

        public static Vector3 Point(Vector3 tracking) => worldOrigin + Rotation * (tracking - trackOrigin) * Scale;
        public static Quaternion Rot(Quaternion tracking) => Rotation * tracking;
        /// <summary>World point back to tracking space (the inverse of Point).</summary>
        public static Vector3 ToTracking(Vector3 world) => trackOrigin + Quaternion.Inverse(Rotation) * (world - worldOrigin) / Scale;

        // OpenXR is right-handed with -Z forward; Unity is left-handed with +Z forward.
        public static Vector3 ToUnity(XrNative.Vec3 p) => new Vector3(p.x, p.y, -p.z);
        public static Quaternion ToUnity(XrNative.Quat q) => new Quaternion(-q.x, -q.y, q.z, q.w);
    }
}
