using UnityEngine;
using DLS.Simulation; // Access to the Simulator's queue
using System.Collections.Generic;

public class SpeakerManager : MonoBehaviour
{
    [Tooltip("The Speaker Prefab to spawn")]
    public GameObject speakerPrefab; // You will drag your prefab here in the Inspector

    // A dictionary to keep track of the speakers we've created
    private Dictionary<uint, GameObject> activeSpeakers = new Dictionary<uint, GameObject>();

    void Update()
    {
        //Debug.Log("SpeakerManager is checking queue..."); // DEBUG
        // Check the queue on every frame
        while (Simulator.SpeakerCommandQueue.TryDequeue(out MainThreadSpeakerCommand cmd))
        {
            if (cmd.CommandType == SpeakerCommandType.Create)
            {
                // --- CREATE SPEAKER ---
                if (speakerPrefab != null && !activeSpeakers.ContainsKey(cmd.SpeakerID))
                {
                    Debug.Log($"Creating Speaker, ID: {cmd.SpeakerID}");
                    GameObject newSpeakerObj = Instantiate(speakerPrefab, this.transform);
                    newSpeakerObj.name = $"DLS Speaker {cmd.SpeakerID}";

                    // Get the speaker script and set its ID
                    DynamicSpeaker speakerScript = newSpeakerObj.GetComponent<DynamicSpeaker>();
                    if (speakerScript != null)
                    {
                        speakerScript.speakerID = cmd.SpeakerID;
                    }

                    // Store it so we can destroy it later
                    activeSpeakers.Add(cmd.SpeakerID, newSpeakerObj);
                }
            }
            else if (cmd.CommandType == SpeakerCommandType.Destroy)
            {
                // --- DESTROY SPEAKER ---
                if (activeSpeakers.TryGetValue(cmd.SpeakerID, out GameObject speakerToDestroy))
                {
                    Debug.Log($"Destroying Speaker, ID: {cmd.SpeakerID}");
                    Destroy(speakerToDestroy);
                    activeSpeakers.Remove(cmd.SpeakerID);
                }
            }
        }
    }
}