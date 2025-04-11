#define METHOD4

using UnityEditor;
using UnityEngine;
using System.Diagnostics;
using System.IO;
using System.Collections;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

namespace NiloToon.NiloToonURP
{
    public class NiloEditorMotionBlurVideoBaker : EditorWindow
    {
        private string ffmpegPath;
        private string inputFilePath = string.Empty;
        private string customSuffix = "(BakedMotionBlur)_<fps>_<shutterspeed>"; // Custom suffix for the output file
        private int crf = 0; // Default CRF value
        private List<float> fpsOptions = new List<float>(); // List of possible FPS options
        private int selectedFPSIndex = 0; // Default FPS index
        private float inputVideoFPS = 0.0f; // FPS of the input video
        private float cameraExposureDuration = 1f / 48f; // 180 shutter angle in terms of 24fps
        private float motionBlurAmount = 1;

        private string ffmpegArgumentsTemplate =
            "-vf \"{0}format=yuv420p\" -r {1} -c:v libx264 -preset veryslow -crf {2} -pix_fmt yuv420p -x264opts \"keyint=12:min-keyint=1:ref=1:bframes=0:qcomp=0.8:aq-strength=0.5:direct=auto:fast-pskip=0:deblock=-2,-2\"";

        private Process ffmpegProcess;
        private Coroutine coroutine;
        private string outputFilePath;
        private string errorMessage;
        private int currenttmixFrameCount = 0;

        private const string FFmpegPathKey = "NiloToon_MotionBlurVideoBakerPath"; // Key for storing FFmpeg path in EditorPrefs

        private Vector2 scrollPosition;

        [MenuItem("Window/NiloToonURP/MotionBlur Video Baker", priority = 10000)]
        public static void ShowWindow()
        {
            var window = GetWindow<NiloEditorMotionBlurVideoBaker>("MotionBlur Video Baker");
            window.LoadFFmpegPath(); // Load the FFmpeg path when the window is opened
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            // 960fps will make cinemachine not working correctly, so we suggest max at 900fps
            EditorGUILayout.HelpBox(
                            "[Purpose of this tool]\n" +
                                    "Bake 480-900 fps video with zero or little motion blur -> 24/30/60 fps video with cinematic motion blur & AA produced by sub frame merging\n\n" +
                                    "[How to use?]\n" +
                                    "1.Locate your own ffmpeg.exe (download from ffmpeg.org)\n" +
                                    "2.Prepare a video with 480~900FPS (e.g., Record using Unity's Recorder with a high custom FPS)\n" +
                                    "3.Select that video as Input Video, wait for analysis\n" +
                                    "4.(optional)Adjust other settings if needed\n" +
                                    "5.Click 'Bake now!', this tool will bake cinematic motion blur & AA to a 24/30/60 fps output video (H.264)", MessageType.Info);

            GUI.enabled = ffmpegProcess == null;
            
            ////////////////////////////////////////////////////////////////////////
            // FFmpeg Path
            ////////////////////////////////////////////////////////////////////////
            GUILayout.Label("FFmpeg Path", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            ffmpegPath = EditorGUILayout.TextField("Path",ffmpegPath);
            if (GUILayout.Button("...", GUILayout.Width(60)))
            {
                string selectedPath = EditorUtility.OpenFilePanel("Select FFmpeg Executable", "", "exe");
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    ffmpegPath = selectedPath;
                    SaveFFmpegPath();
                }
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(25);

            ////////////////////////////////////////////////////////////////////////
            // Input Video Path
            ////////////////////////////////////////////////////////////////////////
            GUILayout.Label("Input Video", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            inputFilePath = EditorGUILayout.TextField("Path",inputFilePath);
            if (GUILayout.Button("...", GUILayout.Width(30)) && ffmpegProcess == null)
            {
                string newPath = EditorUtility.OpenFilePanel("Select Input File", "", "mov,mp4");
                if (!string.IsNullOrEmpty(newPath))
                {
                    inputFilePath = newPath;
                    ExtractVideoFPS(inputFilePath); // Extract the FPS of the selected video
                }
            }
            EditorGUILayout.EndHorizontal();

            if (inputVideoFPS < 480)
            {
                EditorGUILayout.HelpBox("Expect a video with 480~900FPS", MessageType.Info);
            }

            // Display error message if input video path is invalid
            if (!string.IsNullOrEmpty(inputFilePath) && !File.Exists(inputFilePath))
            {
                EditorGUILayout.HelpBox("Input video file not found at the specified path.", MessageType.Error);
            }

            ////////////////////////////////////////////////////////////////////////
            // input video fps
            ////////////////////////////////////////////////////////////////////////
            if (inputVideoFPS > 0 && File.Exists(inputFilePath))
            {
                GUILayout.Label($"Input Video FPS: {inputVideoFPS}", EditorStyles.boldLabel);
            }

            GUILayout.Space(25);

            ////////////////////////////////////////////////////////////////////////
            // output video (suffix,fps,CRF)
            ////////////////////////////////////////////////////////////////////////
           
            GUILayout.Label("Output Video", EditorStyles.boldLabel);

            if (fpsOptions.Count > 0)
            {
                selectedFPSIndex = Mathf.Clamp(selectedFPSIndex, 0, fpsOptions.Count - 1);
                int newSelectedFPSIndex = EditorGUILayout.Popup("FPS", selectedFPSIndex, fpsOptions.ConvertAll(f => f.ToString()).ToArray());
                if (newSelectedFPSIndex != selectedFPSIndex)
                {
                    selectedFPSIndex = newSelectedFPSIndex;
                    
                    UnityEngine.Debug.Log($"Selected FPS: {GetOutputFPS()}"); // Log the selected FPS
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No valid FPS options available.", MessageType.Warning);
            }

            motionBlurAmount = EditorGUILayout.Slider("Motion Blur", motionBlurAmount, 0f, 4f);
            crf = EditorGUILayout.IntSlider("CRF", crf, 0, 51);

            EditorGUILayout.HelpBox(
                "CRF (Constant Rate Factor) controls the quality of the video. Lower values mean higher quality and larger file sizes. The range is from 0 (lossless) to 51 (worst quality).",
                MessageType.Info);

            customSuffix = EditorGUILayout.TextField("Suffix", customSuffix);
            
            ////////////////////////////////////////////////////////////////////////
            // output video info
            ////////////////////////////////////////////////////////////////////////
            if (fpsOptions.Count > 0)
            {
                Generate_tmixFilters(inputVideoFPS);
                
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Output info:", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    $"- TMix Frame Count: {currenttmixFrameCount}\n" +
                            $"- Shutter Angle in terms of 24fps: {GetShutterAngleInTermsOf24fps()} degrees (~180 is a good default for cinematic output)\n" +
                            $"- Shutter Speed: {GetShutterSpeedDisplayString()} (~1/48 is a good default for cinematic output)"
                    , MessageType.Info);

                // check if shutter angle is too small
                if (currenttmixFrameCount < Mathf.CeilToInt(inputVideoFPS / GetOutputFPS()/ 2))
                {
                    EditorGUILayout.HelpBox(
                        $"Current Shutter Angle for {GetOutputFPS()}fps is < 180, while it is not wrong, it may produce not enough motion blur"
                        , MessageType.Warning);
                }
                
                // it is ok to go over 360, so don't give warning.
                // for example, 60fps output with 1/48th shutter speed
                /*
                // check if shutter angle is too big
                if (currenttmixFrameCount > (inputVideoFPS / GetOutputFPS()))
                {
                    EditorGUILayout.HelpBox(
                        $"Current Shutter Angle is too large, it may produce too much motion blur"
                        , MessageType.Warning);
                }
                */
            }

            GUILayout.Space(25);
            
            EditorGUILayout.LabelField("Extra note:", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"- Make sure Project Settings > VFX > Fixed Time Step is using '1/fps of recorder' when recording\n" +
                $"- Do not record in 960fps or higher, it may break cinemachine's camera movement"
                , MessageType.Info);

            GUI.enabled = true;
            ////////////////////////////////////////////////////////////////////////
            // Bake button
            ////////////////////////////////////////////////////////////////////////
            
            // Disable the "Bake Now!" button if any condition is not met
            GUI.enabled = ffmpegProcess == null && ValidatePaths() && fpsOptions.Count > 0;
            if (GUILayout.Button("Bake now!") && ffmpegProcess == null && ValidatePaths() && fpsOptions.Count > 0)
            {
                SaveFFmpegPath(); // Save the FFmpeg path when starting the conversion

                // Generate the output file path based on the input file path and custom suffix
                outputFilePath = GenerateOutputFilePath(inputFilePath, customSuffix);

                // Check if the output file already exists
                if (File.Exists(outputFilePath))
                {
                    // Ask the user if they want to overwrite the existing file
                    bool overwrite = EditorUtility.DisplayDialog(
                        "File Exists",
                        "The output file already exists. Do you want to overwrite it?",
                        "Yes",
                        "No"
                    );

                    if (!overwrite)
                    {
                        return;
                    }

                    // Delete the existing file
                    File.Delete(outputFilePath);
                }

                // Start the FFmpeg process as a coroutine
                coroutine = EditorCoroutine.StartCoroutine(StartFFmpegProcessCoroutine(outputFilePath, GetOutputFPS()));
            }

            GUI.enabled = true;

            if (ffmpegProcess != null && GUILayout.Button("Cancel"))
            {
                CancelFFmpegProcess();
            }
            
            EditorGUILayout.EndScrollView();
        }

        private bool ValidatePaths()
        {
            bool valid = true;
            errorMessage = string.Empty;

            if (!File.Exists(ffmpegPath))
            {
                errorMessage += "FFmpeg executable not found at the specified path.\n";
                valid = false;
            }

            if (!File.Exists(inputFilePath))
            {
                errorMessage += "Input video file not found at the specified path.\n";
                valid = false;
            }

            return valid;
        }

        private void SaveFFmpegPath()
        {
            EditorPrefs.SetString(FFmpegPathKey, ffmpegPath);
        }

        private void LoadFFmpegPath()
        {
            ffmpegPath = EditorPrefs.GetString(FFmpegPathKey, @"C:\path\to\ffmpeg.exe");
        }

        private float GetOutputFPS()
        {
            return fpsOptions[selectedFPSIndex];
        }

        private float GetShutterAngleInTermsOf24fps()
        {
            return (float)currenttmixFrameCount / (inputVideoFPS / 24f) * 360f;
        }
        private float GetShutterSpeed()
        {
            return (1f / 24f) * (GetShutterAngleInTermsOf24fps() / 360f);
        }

        private string GetShutterSpeedDisplayString()
        {
            return $"1/{1 / GetShutterSpeed()}s";
        }

        private string GetShutterSpeedFileNameString()
        {
            return $"OneOver{1 / GetShutterSpeed()}s";
        }
        private string GenerateOutputFilePath(string inputPath, string suffix)
        {
            suffix = suffix.Replace("<fps>", $"{GetOutputFPS()}fps");
            suffix = suffix.Replace("<shutterspeed>", $"{GetShutterSpeedFileNameString()}");
            string directory = Path.GetDirectoryName(inputPath);
            string filenameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
            string extension = Path.GetExtension(inputPath);
            return Path.Combine(directory, $"{filenameWithoutExtension}{suffix}{extension}");
        }

        private IEnumerator StartFFmpegProcessCoroutine(string outputFilePath, float selectedFPS)
        {
            string tmixFilters = Generate_tmixFilters(inputVideoFPS);
            string ffmpegArguments = string.Format(ffmpegArgumentsTemplate, tmixFilters, selectedFPS, crf);

            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{inputFilePath}\" {ffmpegArguments} \"{outputFilePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false, // must be false
                CreateNoWindow = true
            };

            ffmpegProcess = new Process
            {
                StartInfo = processStartInfo
            };
            ffmpegProcess.OutputDataReceived += (sender, args) => HandleFFmpegOutput(args.Data);
            ffmpegProcess.ErrorDataReceived += (sender, args) => HandleFFmpegOutput(args.Data);

            ffmpegProcess.Start();
            ffmpegProcess.BeginOutputReadLine();
            ffmpegProcess.BeginErrorReadLine();

            // Wait for the process to exit while updating the progress bar
            while (!ffmpegProcess.HasExited)
            {
                yield return null;
            }

            ffmpegProcess.WaitForExit();
            ffmpegProcess = null;

            // Close the progress bar when the process exits
            EditorUtility.ClearProgressBar();
        }

        private string Generate_tmixFilters(float inputVideoFPS)
        {
            int numberOfTMixFrames = Mathf.FloorToInt(inputVideoFPS * cameraExposureDuration * motionBlurAmount);
            currenttmixFrameCount = numberOfTMixFrames;

            if (currenttmixFrameCount == 0)
            {
                return "";
            }
            
            #if METHOD1
            string weightString = string.Join(" ", Enumerable.Repeat("1", numberOfTMixFrames));
            #endif
            
            #if METHOD2
            // [2-way expo falloff]
            
            // Generate exponential falloff weights
            List<int> weights = new List<int>();
            int centerFrame = numberOfTMixFrames / 2;
            float falloffRate = 0.5f; // Adjust this value to control the steepness of the falloff

            for (int i = 0; i < numberOfTMixFrames; i++)
            {
                float distance = Mathf.Abs(i - centerFrame);
                int weight = Mathf.RoundToInt(100 * Mathf.Exp(-falloffRate * distance));
                weights.Add(weight);
            }

            // Ensure the center weight is always 100
            weights[centerFrame] = 100;

            string weightString = string.Join(" ", weights);
            //-------------------------------------------------------------------------------------------------------
            #endif
            
            
            #if METHOD3
            //-------------------------------------------------------------------------------------------------------
            // [1-way expo falloff]

            // Generate exponential falloff weights
            List<int> weights = new List<int>();
            float falloffRate = 2.5f / numberOfTMixFrames; // Adjust this value to control the steepness of the falloff

            for (int i = 0; i < numberOfTMixFrames; i++)
            {
                float weight = Mathf.Exp(falloffRate * i);
                weights.Add(Mathf.RoundToInt(weight * 100)); // Scale up and round to get integer weights
            }

            // Normalize weights to ensure the last (most recent) weight is always 100
            int maxWeight = weights[weights.Count - 1];
            for (int i = 0; i < weights.Count; i++)
            {
                weights[i] = Mathf.RoundToInt((float)weights[i] / maxWeight * 100);
            }

            // Ensure no weight is zero
            weights = weights.Select(w => Mathf.Max(w, 1)).ToList();
            
            string weightString = string.Join(" ", weights);
            //-------------------------------------------------------------------------------------------------------
            #endif
            
            #if METHOD4
            string weightString = GenerateGaussianWeights(numberOfTMixFrames);
            #endif
            return $"tmix=frames={numberOfTMixFrames}:weights='{weightString}',";
        }
        
        private string GenerateGaussianWeights(int numberOfTMixFrames)
        {
            numberOfTMixFrames = Mathf.Min(40, Mathf.Max(1, numberOfTMixFrames));
            float sigma = numberOfTMixFrames / 6f; // Adjust sigma based on frame count

            List<float> weights = new List<float>();
            float sum = 0;

            for (int i = 0; i < numberOfTMixFrames; i++)
            {
                float x = (i - (numberOfTMixFrames - 1) / 2f) / sigma;
                float weight = Mathf.Exp(-(x * x) / 2f);
                weights.Add(weight);
                sum += weight;
            }
            
            const int scale = 1000;
            List<int> scaledWeights = weights.Select(w => (int)Mathf.Round(w / sum * scale)).ToList();

            int currentSum = scaledWeights.Sum();
            if (currentSum != scale)
            {
                int diff = scale - currentSum;
                int middleIndex = scaledWeights.Count / 2;
                scaledWeights[middleIndex] += diff;
            }

            return string.Join(" ", scaledWeights);
        }

        private void CancelFFmpegProcess()
        {
            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                ffmpegProcess.Kill();
                ffmpegProcess.WaitForExit(); // Ensure the process has completely exited
                ffmpegProcess = null;
                if (coroutine != null)
                {
                    EditorCoroutine.StopCoroutine(coroutine);
                    coroutine = null;
                }

                EditorUtility.ClearProgressBar();
                UnityEngine.Debug.Log("FFmpeg process cancelled.");

                // Delete the incomplete output file
                if (File.Exists(outputFilePath))
                {
                    try
                    {
                        File.Delete(outputFilePath);
                        UnityEngine.Debug.Log("Incomplete output file deleted.");
                    }
                    catch (IOException e)
                    {
                        UnityEngine.Debug.LogError($"Failed to delete incomplete output file: {e.Message}");
                    }
                }
            }
        }

        private void HandleFFmpegOutput(string data)
        {
            UnityEngine.Debug.Log(data);
        }

        private void ExtractVideoFPS(string videoPath)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{videoPath}\" -vcodec copy -f null -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process process = new Process
            {
                StartInfo = processStartInfo
            };

            process.OutputDataReceived += ProcessOutputHandler;
            process.ErrorDataReceived += ProcessOutputHandler;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
        }

        private void ProcessOutputHandler(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            UnityEngine.Debug.Log("FFmpeg Output: " + e.Data); // Log the output to debug

            // Example of how to parse the FPS from FFmpeg output
            // Look for a line containing "fps" and extract the value
            var match = Regex.Match(e.Data, @"(\d+(\.\d+)?) fps");
            if (match.Success)
            {
                if (float.TryParse(match.Groups[1].Value, out float fps))
                {
                    inputVideoFPS = fps;
                    UnityEngine.Debug.Log("Extracted FPS: " + inputVideoFPS); // Log the extracted FPS
                    UpdateFPSOptions(inputVideoFPS); // Update FPS options based on extracted FPS
                }
            }
        }

        private void UpdateFPSOptions(float inputFPS)
        {
            fpsOptions.Clear();
            float[] supportedOutputFPS = { 24, 25, 30, 50, 60 };

            foreach (var fps in supportedOutputFPS)
            {
                if (inputFPS >= fps * 2)
                {
                    fpsOptions.Add(fps);
                }
            }

            selectedFPSIndex = fpsOptions.Count - 1; // Set the highest FPS
        }
    }

    // Helper class to start coroutines in the editor
    public static class EditorCoroutine
    {
        private class CoroutineHolder : MonoBehaviour { }

        private static CoroutineHolder coroutineHolder;

        public static Coroutine StartCoroutine(IEnumerator coroutine)
        {
            if (coroutineHolder == null)
            {
                GameObject newGO = new GameObject("EditorCoroutine");
                newGO.hideFlags = HideFlags.HideAndDontSave;
                coroutineHolder = newGO.AddComponent<CoroutineHolder>();
                coroutineHolder.hideFlags = HideFlags.HideAndDontSave;
            }

            return coroutineHolder.StartCoroutine(coroutine);
        }

        public static void StopCoroutine(Coroutine coroutine)
        {
            if (coroutineHolder != null)
            {
                coroutineHolder.StopCoroutine(coroutine);
            }
        }
    }
}