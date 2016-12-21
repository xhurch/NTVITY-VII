﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]

public class LaserSound : MonoBehaviour {

    public AudioSource LaserAudio;

    private SteamVR_Controller.Device controller { get { return SteamVR_Controller.Input((int)trackedObj.index); } }
    private SteamVR_TrackedObject trackedObj;

 
    void Start ()
    {
      trackedObj = GetComponent<SteamVR_TrackedObject>();
    }
 
    void Update()
    {
        if (controller == null)
        {
            Debug.Log("Controller not initialized");
            return;
        }
        if (controller.GetPress(SteamVR_Controller.ButtonMask.Touchpad))
        {
            Debug.Log("Pew Pew");
            LaserAudio.Play();
            }

        }
}
