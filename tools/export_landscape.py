"""Перетворює openmmlab/upernet-convnext-small (ADE20K, MIT) у ONNX для масок «Ландшафт».

Вихід моделі — не 150 класів, а ймовірності груп (softmax, сума по класах групи),
щоб із відеокарти читати лише кілька каналів.
"""
import sys
import torch
from transformers import UperNetForSemanticSegmentation

out_path = sys.argv[1]

GROUPS = [
    ("water", ["water", "sea", "river", "lake", "swimming pool", "waterfall"]),
    ("vegetation", ["tree", "grass", "plant", "palm", "flower"]),
    ("mountain", ["mountain", "hill", "rock"]),
    ("architecture", ["building", "house", "skyscraper", "tower", "bridge", "hovel"]),
    ("natural_ground", ["earth", "sand", "field", "dirt track", "land"]),
    ("artificial_ground", ["road", "sidewalk", "floor", "path", "runway", "stairs", "stairway"]),
]

model = UperNetForSemanticSegmentation.from_pretrained("openmmlab/upernet-convnext-small")
model.eval()
labels = {v.lower(): int(k) for k, v in model.config.id2label.items()}
print("labels:", sorted(labels))

index_lists = []
for name, wanted in GROUPS:
    ids = []
    for w in wanted:
        hits = [i for lbl, i in labels.items() if w == lbl or lbl.startswith(w + ";") or (";" + w) in lbl]
        if not hits:
            print(f"  [{name}] немає класу '{w}'")
        ids += hits
    ids = sorted(set(ids))
    print(name, ids, [model.config.id2label[i] for i in ids])
    index_lists.append(ids)


class Groups(torch.nn.Module):
    def __init__(self, m, groups):
        super().__init__()
        self.m = m
        n = m.config.num_labels
        w = torch.zeros(len(groups), n)
        for g, ids in enumerate(groups):
            w[g, ids] = 1.0
        self.register_buffer("w", w)

    def forward(self, pixel_values):
        # Логіти в 1/4 роздільності, без розтягування до розміру входу: softmax по 150 класах на 768×768 —
        # сотні мегабайтів на відеокарті. Збільшення до кадру й підгонку країв робить сама програма.
        features = self.m.backbone.forward_with_filtered_kwargs(pixel_values).feature_maps
        logits = self.m.decode_head(features)  # 1×150×H/4×W/4
        p = torch.softmax(logits, dim=1)
        return torch.einsum("gc,bchw->bghw", self.w, p)


wrapped = Groups(model, index_lists).eval()
# PSP-пулінг (1, 2, 3, 6) на ознаках 1/32: 768 / 32 = 24 ділиться на всі
dummy = torch.randn(1, 3, 768, 768)
with torch.no_grad():
    torch.onnx.export(
        wrapped, (dummy,), out_path,
        input_names=["pixel_values"], output_names=["groups"],
        opset_version=17, dynamo=False)  # PSP-пулінг не експортується з динамічним розміром — вхід 512×512
print("saved", out_path)
