"""Train the PVOMR glyph classifier on crops produced by `pvomr crops` and export it to ONNX.

usage: python tools/train_glyphs.py --crops <dir> [--out models/glyphs.onnx] [--epochs 20] [--test-crops <dir>]

The crops directory holds 40x40 PNGs (ink = white) and index.csv with columns file,label,w_ss,h_ss,source.
"""
import argparse, csv, os, random, sys
import numpy as np
import torch, torch.nn as nn, torch.nn.functional as F
from PIL import Image

LABELS = ["head_black", "head_half", "head_whole", "rest_whole", "rest_half", "rest_quarter", "rest_8th", "rest_16th",
          "clef_g", "clef_f", "clef_c", "acc_sharp", "acc_flat", "acc_natural", "digit", "timesig_common", "dot", "dynamic",
          "text", "slur", "line", "beam", "flag", "fragment", "composite", "other"]
SIZE = 40


def load(dirs):
    xs, feats, ys, srcs = [], [], [], []
    for d in dirs:
        with open(os.path.join(d, "index.csv"), newline="") as f:
            for row in csv.DictReader(f):
                if row["label"] not in LABELS:
                    continue
                img = np.asarray(Image.open(os.path.join(d, row["file"])).convert("L"), dtype=np.float32) / 255.0
                xs.append(img)
                feats.append([float(row["w_ss"]), float(row["h_ss"])])
                ys.append(LABELS.index(row["label"]))
                srcs.append(row["source"])
    return np.stack(xs)[:, None, :, :], np.array(feats, dtype=np.float32), np.array(ys), srcs


class Net(nn.Module):
    def __init__(self, n_classes):
        super().__init__()
        self.c1 = nn.Conv2d(1, 24, 3, padding=1)
        self.c2 = nn.Conv2d(24, 48, 3, padding=1)
        self.c3 = nn.Conv2d(48, 96, 3, padding=1)
        self.bn1, self.bn2, self.bn3 = nn.BatchNorm2d(24), nn.BatchNorm2d(48), nn.BatchNorm2d(96)
        self.fc1 = nn.Linear(96 * 5 * 5 + 2, 192)
        self.drop = nn.Dropout(0.3)
        self.fc2 = nn.Linear(192, n_classes)

    def forward(self, image, features):
        x = F.max_pool2d(F.relu(self.bn1(self.c1(image))), 2)     # 20
        x = F.max_pool2d(F.relu(self.bn2(self.c2(x))), 2)         # 10
        x = F.max_pool2d(F.relu(self.bn3(self.c3(x))), 2)         # 5
        x = x.flatten(1)
        x = torch.cat([x, torch.log1p(features)], dim=1)
        x = self.drop(F.relu(self.fc1(x)))
        return self.fc2(x)


def augment(batch):
    # random shifts by up to 2 px and occasional horizontal stretch, on the batch tensor
    b = batch.clone()
    for i in range(b.shape[0]):
        dx, dy = random.randint(-2, 2), random.randint(-2, 2)
        b[i] = torch.roll(b[i], shifts=(dy, dx), dims=(1, 2))
        if random.random() < 0.3:
            b[i] = torch.clamp(b[i] + torch.randn_like(b[i]) * 0.08, 0, 1)
    return b


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--crops", nargs="+", required=True)
    ap.add_argument("--test-crops", nargs="*", default=[])
    ap.add_argument("--out", default="models/glyphs.onnx")
    ap.add_argument("--epochs", type=int, default=20)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--labels", default=None, help="comma separated label set (default: glyph classes)")
    args = ap.parse_args()
    global LABELS
    if args.labels:
        LABELS = args.labels.split(",")
    torch.manual_seed(args.seed); random.seed(args.seed); np.random.seed(args.seed)

    x, f, y, srcs = load(args.crops)
    print(f"{len(y)} crops, {len(set(y))} classes present")
    counts = np.bincount(y, minlength=len(LABELS))
    for i, c in enumerate(counts):
        if c: print(f"  {LABELS[i]:16s} {c}")
    # split by source piece (hold out 15% of sources) unless a test set is given
    if args.test_crops:
        xt, ft, yt, _ = load(args.test_crops)
        xtr, ftr, ytr = x, f, y
    else:
        uniq = sorted(set(srcs)); random.shuffle(uniq)
        hold = set(uniq[: max(1, len(uniq) // 7)])
        mask = np.array([s in hold for s in srcs])
        xtr, ftr, ytr, xt, ft, yt = x[~mask], f[~mask], y[~mask], x[mask], f[mask], y[mask]
    print(f"train {len(ytr)}, test {len(yt)}")

    # class weights: damp the dominant classes
    w = 1.0 / np.sqrt(np.maximum(1, np.bincount(ytr, minlength=len(LABELS))))
    w = torch.tensor(w / w.mean(), dtype=torch.float32)
    net = Net(len(LABELS))
    opt = torch.optim.AdamW(net.parameters(), lr=2e-3, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=3e-3, total_steps=args.epochs * ((len(ytr) + 127) // 128))
    Xtr, Ftr, Ytr = torch.tensor(xtr), torch.tensor(ftr), torch.tensor(ytr)
    Xt, Ft, Yt = torch.tensor(xt), torch.tensor(ft), torch.tensor(yt)
    for epoch in range(args.epochs):
        net.train()
        perm = torch.randperm(len(Ytr))
        tot, correct, loss_sum = 0, 0, 0.0
        for i in range(0, len(perm), 128):
            idx = perm[i:i + 128]
            xb, fb, yb = augment(Xtr[idx]), Ftr[idx], Ytr[idx]
            fb = fb * (1 + torch.randn_like(fb) * 0.05)
            out = net(xb, fb)
            loss = F.cross_entropy(out, yb, weight=w)
            opt.zero_grad(); loss.backward(); opt.step(); sched.step()
            loss_sum += loss.item() * len(idx); tot += len(idx); correct += (out.argmax(1) == yb).sum().item()
        net.eval()
        with torch.no_grad():
            pred = net(Xt, Ft).argmax(1)
            acc = (pred == Yt).float().mean().item() if len(Yt) else float("nan")
        print(f"epoch {epoch + 1:2d}: loss {loss_sum / tot:.3f} train acc {correct / tot:.3f} test acc {acc:.3f}")
    # per-class test report
    net.eval()
    with torch.no_grad():
        pred = net(Xt, Ft).argmax(1).numpy()
    print("per-class test accuracy:")
    for i, name in enumerate(LABELS):
        m = yt == i
        if m.sum():
            wrong = np.bincount(pred[m], minlength=len(LABELS)); wrong[i] = 0
            top = LABELS[int(wrong.argmax())] if wrong.sum() else "-"
            print(f"  {name:16s} n={m.sum():5d} acc={(pred[m] == i).mean():.3f} most confused with {top}")
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    dummy = (torch.zeros(1, 1, SIZE, SIZE), torch.zeros(1, 2))
    torch.onnx.export(net, dummy, args.out, input_names=["image", "features"], output_names=["logits"],
                      dynamic_axes={"image": {0: "batch"}, "features": {0: "batch"}, "logits": {0: "batch"}}, opset_version=17, dynamo=False)
    with open(os.path.splitext(args.out)[0] + ".labels.txt", "w") as fh:
        fh.write("\n".join(LABELS) + "\n")
    print(f"exported {args.out}")


if __name__ == "__main__":
    main()
