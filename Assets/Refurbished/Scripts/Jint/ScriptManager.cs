using UnityEngine;
using Jint; // You need Jint
using System.IO;
using System.Collections.Concurrent;
using DLS.Simulation; // <-- This lets it see your Simulator class

public class ScriptManager : MonoBehaviour
{
    void Awake()
    {
        // Define the folder where your scripts live
        string scriptPath = Path.Combine(Application.streamingAssetsPath, "Scripts");
        if (!Directory.Exists(scriptPath))
        {
            Directory.CreateDirectory(scriptPath);
            Debug.Log("Created script directory: " + scriptPath);
            return;
        }

        // Find all script files ("script_0.js", "script_1.js", etc.)
        string[] scriptFiles = Directory.GetFiles(scriptPath, "script_*.js");

        foreach (string filePath in scriptFiles)
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            if (uint.TryParse(fileName.Replace("script_", ""), out uint scriptID))
            {
                Debug.Log($"Loading Jint script with ID: {scriptID}");
                try
                {
                    string scriptContent = File.ReadAllText(filePath);

                    var engine = new Engine(cfg => cfg.AllowClr());

                    // Pre-define the input/output arrays
                    engine.SetValue("inputs", new object[16]);
                    engine.SetValue("outputs", new object[16]);
                    engine.SetValue("memory", new object[256]);

                    // Define the 'update()' function
                    engine.Execute(scriptContent);

                    // --- THIS IS THE KEY ---
                    // This script populates the dictionary that lives in your Simulator class
                    Simulator.ScriptEngines[scriptID] = engine;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Error loading script {fileName}: {e.Message}");
                }
            }
        }
    }
}