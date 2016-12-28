//
// Neuron animator class
//
// Refactored by Keijiro Takahashi
// https://github.com/keijiro/NeuronRetargeting
//
// This is a derivative work of the Perception Neuron SDK. You can use this
// freely as one of "Perception Neuron SDK Derivatives". See LICENSE.pdf and
// their website for further details.
//

using UnityEngine;
using Neuron;

[RequireComponent(typeof(Animator))]
[AddComponentMenu("Perception Neuron/Neuron Animator")]
public class NeuronAnimator : MonoBehaviour
{
    #region Editable variables

    [SerializeField]
    string _address = "127.0.0.1";

    [SerializeField]
    int _port = 7001;

    [SerializeField]
    NeuronConnection.SocketType	_socketType = NeuronConnection.SocketType.TCP;

    [SerializeField]
    int _actorID = 0;

    #endregion

    #region MonoBehavior functions

    void Awake()
    {
        _animator = GetComponent<Animator>();
        ScanBones();
    }

    void OnEnable()
    {
        _source = NeuronConnection.Connect(_address, _port, _socketType);
        if (_source != null) _actor = _source.AcquireActor(_actorID);
    }

    void OnDisable()
    {
        if (_source != null) NeuronConnection.Disconnect(_source);
        _source = null;
        _actor = null;
    }

    void Update()
    {
        if (_actor == null) return;

        UpdateRoot();

        // Legs
        UpdateBoneRotation(HumanBodyBones.RightUpperLeg, NeuronBones.RightUpLeg);
        UpdateBoneRotation(HumanBodyBones.RightLowerLeg, NeuronBones.RightLeg);
        UpdateBoneRotation(HumanBodyBones.RightFoot,     NeuronBones.RightFoot);
        UpdateBoneRotation(HumanBodyBones.LeftUpperLeg,  NeuronBones.LeftUpLeg);
        UpdateBoneRotation(HumanBodyBones.LeftLowerLeg,  NeuronBones.LeftLeg);
        UpdateBoneRotation(HumanBodyBones.LeftFoot,      NeuronBones.LeftFoot);

        // Spine
        UpdateBoneRotation(HumanBodyBones.Spine, NeuronBones.Spine);
        UpdateBoneRotation(HumanBodyBones.Chest, NeuronBones.Spine1,
                                                 NeuronBones.Spine2,
                                                 NeuronBones.Spine3);
        UpdateBoneRotation(HumanBodyBones.Neck, NeuronBones.Neck);
        UpdateBoneRotation(HumanBodyBones.Head, NeuronBones.Head);

        // Right arm
        UpdateBoneRotation(HumanBodyBones.RightShoulder, NeuronBones.RightShoulder);
        UpdateBoneRotation(HumanBodyBones.RightUpperArm, NeuronBones.RightArm);
        UpdateBoneRotation(HumanBodyBones.RightLowerArm, NeuronBones.RightForeArm);
        UpdateBoneRotation(HumanBodyBones.RightHand, NeuronBones.RightHand);

        // Left arm
        UpdateBoneRotation(HumanBodyBones.LeftShoulder, NeuronBones.LeftShoulder);
        UpdateBoneRotation(HumanBodyBones.LeftUpperArm, NeuronBones.LeftArm);
        UpdateBoneRotation(HumanBodyBones.LeftLowerArm, NeuronBones.LeftForeArm);
        UpdateBoneRotation(HumanBodyBones.LeftHand, NeuronBones.LeftHand);
        
        FixUpWithFeetPosition();
    }

    void UpdateRoot()
    {
        var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);

        var pos = _actor.GetReceivedPosition(NeuronBones.Hips);
        var rot = _actor.GetReceivedRotation(NeuronBones.Hips);

        var d_rot = _defaultRotations[(int)HumanBodyBones.Hips];
        var r_rot = _resetRotations[(int)HumanBodyBones.Hips];
        var r_rot_inv = Quaternion.Inverse(r_rot);

        hips.localPosition = r_rot * pos * _scaleFactorForHips;
        hips.localRotation = r_rot * Quaternion.Euler(rot) * r_rot_inv * d_rot;
    }

    void UpdateBoneRotation(
        HumanBodyBones humanBone,
        NeuronBones neuronBone0,
        NeuronBones neuronBone1 = NeuronBones.NumOfBones,
        NeuronBones neuronBone2 = NeuronBones.NumOfBones
    )
    {
        var bone = _animator.GetBoneTransform(humanBone);
        if (bone == null) return;

        var rot = _actor.GetReceivedRotation(neuronBone0);

        if (neuronBone1 != NeuronBones.NumOfBones)
            rot += _actor.GetReceivedRotation(neuronBone1);

        if (neuronBone2 != NeuronBones.NumOfBones)
            rot += _actor.GetReceivedRotation(neuronBone2);

        var d_rot = _defaultRotations[(int)humanBone];
        var r_rot = _resetRotations[(int)humanBone];
        var r_rot_inv = Quaternion.Inverse(r_rot);

        bone.localRotation = r_rot * Quaternion.Euler(rot) * r_rot_inv * d_rot;
    }

    void FixUpWithFeetPosition()
    {
        var lfeet = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        var rfeet = _animator.GetBoneTransform(HumanBodyBones.RightFoot);

        var offs = 0.0f;

        if (lfeet.position.y < rfeet.position.y)
            offs = lfeet.position.y - _animator.leftFeetBottomHeight;
        else
            offs = rfeet.position.y - _animator.rightFeetBottomHeight;

        var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
        hips.position -= Vector3.up * offs;
    }

    #endregion

    #region Private variables

    const int kBoneCount = (int)HumanBodyBones.LastBone;

    Animator _animator;
    NeuronSource _source;
    NeuronActor _actor;

    float _scaleFactorForHips;
    Quaternion[] _resetRotations = new Quaternion[kBoneCount];
    Quaternion[] _defaultRotations = new Quaternion[kBoneCount];

    #endregion

    #region Private functions

    void ScanBones()
    {
        // The height of the hips in the base model.
        const float kBaseHipsHeight = 1.113886f;

        // Get the feet position.
        var lfoot = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        var rfoot = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
        var y_feet = (lfoot.position.y + rfoot.position.y) * 0.5f;
        y_feet -= (_animator.leftFeetBottomHeight + _animator.leftFeetBottomHeight) * 0.5f;

        // Calculate the scale factor.
        var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
        _scaleFactorForHips = (hips.position.y - y_feet) / kBaseHipsHeight;

        // Retrieve bone rotations.
        for (var i = 0; i < kBoneCount; ++i)
        {
            var bone = _animator.GetBoneTransform((HumanBodyBones)i);
            if (bone == null) continue;

            // Default rotation
            _defaultRotations[i] = bone.localRotation;

            // "Reset to standard" rotation
            _resetRotations[i] =
                Quaternion.Inverse(bone.parent.rotation) *
                _animator.transform.rotation;
        }
    }

    #endregion
}
