using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PVOMR.Core.Eval;

namespace PVOMR.Core.Analysis;

/// <summary>Runs the trained glyph CNN (ONNX) on a component mask. Input: 1x40x40 ink grid plus (width, height) in staff spaces.</summary>
public sealed class GlyphClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputImage, _inputFeatures, _output;
    public string[] Labels { get; }
    public string Path { get; }

    private GlyphClassifier(string modelPath, string[] labels)
    {
        Path = modelPath;
        var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 2 };
        _session = new InferenceSession(modelPath, opts);
        var inputs = _session.InputMetadata.Keys.ToList();
        _inputImage = inputs.FirstOrDefault(k => k.Contains("image")) ?? inputs[0];
        _inputFeatures = inputs.FirstOrDefault(k => k.Contains("feat")) ?? inputs[^1];
        _output = _session.OutputMetadata.Keys.First();
        Labels = labels;
    }

    /// <summary>Loads models/&lt;name&gt;.onnx (+ .labels.txt) from the given path, the application directory, the working directory or their parents.</summary>
    public static GlyphClassifier? Load(string? path, List<string> log, string name = "glyphs")
    {
        var candidates = new List<string>();
        if (path != null) candidates.Add(path);
        candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "models", name + ".onnx"));
        candidates.Add(System.IO.Path.Combine(Directory.GetCurrentDirectory(), "models", name + ".onnx"));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent) candidates.Add(System.IO.Path.Combine(dir.FullName, "models", name + ".onnx"));
        foreach (var c in candidates)
        {
            if (!File.Exists(c)) continue;
            string labelsPath = System.IO.Path.ChangeExtension(c, ".labels.txt");
            var labels = File.Exists(labelsPath) ? File.ReadAllLines(labelsPath).Where(l => l.Length > 0).ToArray() : CropExtractor.Labels;
            try
            {
                var clf = new GlyphClassifier(c, labels);
                log.Add($"{name} classifier: {c} ({labels.Length} classes)");
                return clf;
            }
            catch (Exception ex) { log.Add($"glyph classifier {c} failed to load: {ex.Message}"); }
        }
        log.Add($"no {name} classifier model found");
        return null;
    }

    /// <summary>Classifies many grids (Size x Size) in one session run. Returns per grid the class probabilities.</summary>
    public float[][] ClassifyGrids(List<float[]> grids, float f0, float f1)
    {
        int n = grids.Count;
        if (n == 0) return Array.Empty<float[]>();
        var data = new float[n * CropExtractor.Size * CropExtractor.Size];
        for (int i = 0; i < n; i++) Array.Copy(grids[i], 0, data, i * CropExtractor.Size * CropExtractor.Size, CropExtractor.Size * CropExtractor.Size);
        var image = new DenseTensor<float>(data, new[] { n, 1, CropExtractor.Size, CropExtractor.Size });
        var fdata = new float[n * 2];
        for (int i = 0; i < n; i++) { fdata[2 * i] = f0; fdata[2 * i + 1] = f1; }
        var feats = new DenseTensor<float>(fdata, new[] { n, 2 });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputImage, image), NamedOnnxValue.CreateFromTensor(_inputFeatures, feats) };
        using var results = _session.Run(inputs);
        var logits = results.First().AsEnumerable<float>().ToArray();
        int classes = logits.Length / n;
        var outp = new float[n][];
        for (int i = 0; i < n; i++)
        {
            var row = new float[classes];
            float max = float.MinValue;
            for (int k = 0; k < classes; k++) max = Math.Max(max, logits[i * classes + k]);
            float sum = 0;
            for (int k = 0; k < classes; k++) { row[k] = (float)Math.Exp(logits[i * classes + k] - max); sum += row[k]; }
            for (int k = 0; k < classes; k++) row[k] /= sum;
            outp[i] = row;
        }
        return outp;
    }

    public int LabelIndex(string label) => Array.IndexOf(Labels, label);

    public sealed class Prediction
    {
        public string Label = "";
        public float Probability;
        public float[] Probabilities = Array.Empty<float>();
        public string Second = ""; public float SecondProbability;
        public override string ToString() => $"{Label} {Probability:0.00}";
    }

    public Prediction Classify(byte[] mask, int w, int h, double staffSpace)
    {
        var grid = CropExtractor.Normalize(mask, w, h);
        var image = new DenseTensor<float>(grid, new[] { 1, 1, CropExtractor.Size, CropExtractor.Size });
        var feats = new DenseTensor<float>(new[] { (float)(w / staffSpace), (float)(h / staffSpace) }, new[] { 1, 2 });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputImage, image),
            NamedOnnxValue.CreateFromTensor(_inputFeatures, feats),
        };
        using var results = _session.Run(inputs);
        var logits = results.First().AsEnumerable<float>().ToArray();
        // softmax
        float max = logits.Max();
        var exp = logits.Select(v => (float)Math.Exp(v - max)).ToArray();
        float sum = exp.Sum();
        var probs = exp.Select(v => v / sum).ToArray();
        int best = 0, second = -1;
        for (int i = 1; i < probs.Length; i++) if (probs[i] > probs[best]) best = i;
        for (int i = 0; i < probs.Length; i++) if (i != best && (second < 0 || probs[i] > probs[second])) second = i;
        return new Prediction
        {
            Label = best < Labels.Length ? Labels[best] : best.ToString(), Probability = probs[best], Probabilities = probs,
            Second = second >= 0 && second < Labels.Length ? Labels[second] : "", SecondProbability = second >= 0 ? probs[second] : 0,
        };
    }

    public void Dispose() => _session.Dispose();
}
