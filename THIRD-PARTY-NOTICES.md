# Сторонні компоненти Lumos

Lumos використовує такі бібліотеки й моделі. Дякуємо їхнім авторам.

## Моделі ШІ (завантажуються окремо, у %LocalAppData%\Lumos\Models)

| Модель | Для чого | Автори | Ліцензія | Джерело |
|---|---|---|---|---|
| IS-Net «general use» (`isnet-general-use.onnx`) | ШІ-маска «Об'єкт» | Xuebin Qin та ін., проєкт DIS | Apache-2.0 | https://github.com/xuebinqin/DIS (ONNX-файл — з релізу https://github.com/danielgatis/rembg) |
| U²-Net skyseg (`skyseg.onnx`) | ШІ-маска «Небо» | xiongzhu666 (архітектура U²-Net — Xuebin Qin та ін.) | MIT | https://github.com/xiongzhu666/Sky-Segmentation-and-Post-processing (ONNX-файл — https://huggingface.co/JianyuanWang/skyseg) |
| UperNet ConvNeXt-Small, ADE20K (`landscape-upernet-convnext-small.onnx`) | ШІ-маски «Ландшафт»: вода, рослинність, гори, будівлі, ґрунт, покриття | OpenMMLab (архітектури UperNet — Tete Xiao та ін., ConvNeXt — Zhuang Liu та ін.; набір ADE20K — MIT CSAIL) | MIT | https://huggingface.co/openmmlab/upernet-convnext-small (у ONNX перетворено скриптом `tools/export_landscape.py`; готовий файл — у релізі «Lumos-models» репозиторію Lumos, програма завантажує його сама) |
| LaMa / big-lama (`lama_fp32.onnx`) | ШІ-видалення предметів | Roman Suvorov та ін., Samsung AI Center (ONNX — Carve) | Apache-2.0 | https://github.com/advimman/lama (ONNX-файл — https://huggingface.co/Carve/LaMa-ONNX) |
| Depth Anything V2 Small (`depth-anything-v2-small.onnx`) | карта глибини для розмиття об'єктива | Lihe Yang та ін. (ONNX — onnx-community) | Apache-2.0 | https://github.com/DepthAnything/Depth-Anything-V2 (ONNX-файл — https://huggingface.co/onnx-community/depth-anything-v2-small) |

## Бібліотеки

| Бібліотека | Для чого | Ліцензія |
|---|---|---|
| Avalonia | інтерфейс | MIT |
| LibRaw (через Sdcb.LibRaw) | розкодування RAW | LGPL-2.1 або CDDL-1.0 (обгортка Sdcb.LibRaw — MIT) |
| Magick.NET / ImageMagick | JPEG, PNG, TIFF, HEIC, WebP, EXIF | Apache-2.0 / ліцензія ImageMagick |
| SkiaSharp | кодування JPEG | MIT |
| ONNX Runtime (DirectML) | запуск моделей ШІ на відеокарті | MIT |
| ComputeSharp | обробка фото на відеокарті (DirectX 12) | MIT |
| Microsoft.Data.Sqlite | каталог бібліотеки | MIT |
