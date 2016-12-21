using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ViveManager : MonoBehaviour {

	public GameObject head;
	public GameObject leftHand;
	public GameObject rightHand;

	public static ViveManager Instance;

	// Use this for initialization
	void Awake () 
	{
		if (Instance == null)
			Instance = this;
	}
	
	// Update is called once per frame
	void OnDestroy () 
	{
		if (Instance == this)
			Instance = null;
	}
}
