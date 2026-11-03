using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Amiss.Shell
{
    /// <summary>
    /// Hand-only quit control for the probe HUD: keeping an index fingertip
    /// inside <see cref="TouchRadius"/> of <see cref="Target"/> for
    /// <see cref="HoldSeconds"/> quits the application. The dwell and the
    /// countdown make an accidental quit unlikely, so the wearer can leave
    /// the app with hand tracking only and reach the system menus.
    /// </summary>
    public sealed class HandExitButton : MonoBehaviour
    {
        const string IdleText = "TOUCH TO QUIT";

        public RectTransform Target;
        public Image TargetImage;
        public TextMeshProUGUI Label;
        public OVRSkeleton LeftHandSkeleton;
        public OVRSkeleton RightHandSkeleton;
        public float TouchRadius = 0.14f;
        public float HoldSeconds = 1f;

        static readonly Color IdleColor = new Color(0.55f, 0.08f, 0.08f, 1f);
        static readonly Color HoldingColor = new Color(0.95f, 0.55f, 0.05f, 1f);

        Color _idleColor;
        float _hold;

        void Start()
        {
            _idleColor = TargetImage != null ? TargetImage.color : IdleColor;
            ResetVisuals();
        }

        void Update()
        {
            if (Target == null || HoldSeconds <= 0f)
                return;
            if (IsTouching())
            {
                _hold += Time.deltaTime;
                if (_hold >= HoldSeconds)
                {
                    Debug.Log("[Amiss] Hand exit button held; quitting");
                    Application.Quit();
                    return;
                }
                TargetImage.color = Color.Lerp(_idleColor, HoldingColor, _hold / HoldSeconds);
                if (Label != null)
                    Label.text = $"QUIT IN {HoldSeconds - _hold:0.0}s";
            }
            else
            {
                if (_hold > 0f)
                    ResetVisuals();
                _hold = 0f;
            }
        }

        void ResetVisuals()
        {
            if (TargetImage != null)
                TargetImage.color = _idleColor;
            if (Label != null)
                Label.text = IdleText;
        }

        bool IsTouching()
        {
            var center = Target.position;
            return IsTipInside(LeftHandSkeleton, center) || IsTipInside(RightHandSkeleton, center);
        }

        bool IsTipInside(OVRSkeleton skeleton, Vector3 center)
        {
            if (skeleton == null || !skeleton.IsInitialized || !skeleton.IsDataValid ||
                skeleton.Bones == null)
                return false;
            for (var i = 0; i < skeleton.Bones.Count; i++)
            {
                var bone = skeleton.Bones[i];
                if (bone.Id != OVRSkeleton.BoneId.Hand_IndexTip || bone.Transform == null)
                    continue;
                return Vector3.Distance(bone.Transform.position, center) <= TouchRadius;
            }
            return false;
        }
    }
}
