# tools/training

Standalone Python tools for dataset preparation, model training, evaluation, and export. Run the commands below from the repository root. The API and Vision do not run these tools during production.

See the [project README](../../README.md) for application setup, runtime model locations and verification commands.

Training is cloud-first. The development computer is an Intel Core i5-11400F with 32 GB of RAM and an older AMD GPU. Do not plan a training run around a local NVIDIA GPU.

| Priority | Runner | Role |
| --- | --- | --- |
| Primary | Kaggle Notebooks with a GPU | `tools/training/notebooks/smartline_kaggle.ipynb` |
| Secondary | Google Colab | `tools/training/notebooks/smartline_colab.ipynb` |
| Paid fallback | RunPod GPU instance | Same `train.py` command as the notebooks |

The default model family is Ultralytics YOLO26. A new recipe starts with `yolo26n` unless a real validation run shows that a larger model is needed. Choose the task from the inspection problem:

- `detect` for object detection (`tools/training/configs/detect.yaml`)
- Classification is deferred to a later version.
- Segmentation is deferred to a later version.

No training run is included in this repository. There is no labeled dataset yet, and this package does not invent accuracy, mAP, precision, or recall. `tools/training/registry/demobox.json` keeps `metrics: null` until a reviewed run exists.

## What is safe to commit

Commit configuration, notebooks, registry metadata, and checksums. Do not commit images, label caches, `runs/`, weights, ONNX files, or OpenVINO binaries. Those paths are gitignored. Runtime weights belong in `deploy/models/`.

Keep local training data under `tools/training/data/`. The default detection config reads `tools/training/data/demobox/`, containing `data.yaml`, `images/`, and `labels/`. Paths in training configs are relative to the repository root. Only the directory placeholder is tracked by Git.

Camera captures remain in the application's separately configured `DATASET_ROOT`. Preparing a training dataset does not move or delete those recordings.

## Local commands

From the repository root, with no GPU and no dataset:

```bash
python3 tools/training/train.py --config tools/training/configs/detect.yaml
python3 tools/training/evaluate.py --results runs/smartline/demobox-detect/results.csv
python3 tools/training/export.py --weights runs/smartline/demobox-detect/weights/best.pt
python3 tools/training/dataset_tools.py validate --manifest tools/training/data/session/manifest.jsonl --images tools/training/data/session/images
```

`train.py` without `--execute` only prints the plan. It exits with status 2 when `data.yaml` or non-empty labels are missing. `--execute` imports Ultralytics and is intended for the cloud runner.

`evaluate.py` prints the last row of an existing `results.csv`. A missing file is an error.

`export.py` writes ONNX with `model.export(format="onnx")` and then OpenVINO with `model.export(format="openvino")`. Both happen only with `--execute` and a real `best.pt`.

## Dataset tools

`tools/training/dataset_tools.py` checks JPEG and PNG structure, optional Pillow decode when Pillow is installed, exact SHA-256 duplicates, class counts, and severe imbalance (maximum class count at least five times the minimum). The train/validation/test split is deterministic for a seed and includes only rows with `annotated: true`.

YOLO export copies those images and writes one label file per annotated image. An annotated image with an empty box list gets an empty label file, which means "reviewed, no object". An unannotated image is skipped and counted. The tool does not create boxes.

Annotation workflow:

1. Capture camera images with the Dataset page or `POST /api/dataset/sessions/{id}/captures/camera`. The manifest records class, color, size, and defect labels supplied by the operator. `annotated` stays false.
2. Label boxes in [CVAT](https://github.com/cvat-ai/cvat) or [Label Studio](https://github.com/HumanSignal/label-studio). Use the recipe class names (`BOX_RED`, `BOX_BLUE`, `BOX_GREEN` for DemoBox).
3. Export the tool's JSON or YOLO labels.
4. Import by setting `annotated` to true and storing pixel boxes with `sourceWidth` and `sourceHeight` in the manifest, or by placing the tool's YOLO labels beside the matching images.
5. Run `dataset_tools.py validate` and `validate_label_files` before upload. Do not upload a manifest that still says `annotated: false` as if it were labeled.

## Kaggle

1. Create a Kaggle notebook with a GPU accelerator. Do not enable internet secrets or paste API tokens into cells.
2. Add the SmartLine repository and the labeled dataset as notebook inputs. The dataset input must contain `data.yaml`, `images/`, and `labels/` from `export-yolo`.
3. Open `tools/training/notebooks/smartline_kaggle.ipynb`, or upload that notebook.
4. Run the GPU cell. It must print the CUDA device name and total VRAM. If CUDA is unavailable, stop.
5. Run the install cell. It installs the pins in `tools/training/requirements.txt`.
6. Point `DATASET` at the Kaggle input directory and run the structure cell. If labels are missing, the notebook stops. It does not print a score.
7. Run training, then validation. Ultralytics writes `results.csv`, plots, and a confusion matrix for detection when the run succeeds.
8. Run the export cell for ONNX and OpenVINO.
9. Download the run folder from the notebook output. Review the real metrics before registering the model.

## Colab

Use `tools/training/notebooks/smartline_colab.ipynb` when Kaggle is unavailable. Select a GPU runtime, mount or upload the labeled dataset under `/content/dataset`, and follow the same checks. Colab is the fallback, not the primary record of the run.

## RunPod

Start a CUDA pod, clone the repository, and install the same requirements. Do not put credentials in the pod image. Then:

```bash
python3 tools/training/train.py --config tools/training/configs/detect.yaml --execute
python3 tools/training/evaluate.py --results runs/smartline/demobox-detect/results.csv
python3 tools/training/export.py --weights runs/smartline/demobox-detect/weights/best.pt --execute
```

Override `device` in a copy of the config when the pod exposes a GPU index other than the notebook default. Download `best.pt`, the ONNX file, the OpenVINO directory, and `results.csv`.

## Activate a model locally

1. Compare `results.csv` with the acceptance criteria in the Product Recipe.
2. Copy the chosen `.pt`, ONNX, or OpenVINO directory into `deploy/models/`. Do not commit those binaries. Record recipe id, model name, version, task, artifact type, artifact path, SHA-256, class names, training timestamp, training run id, measured metrics, input size, confidence threshold, and export format in `tools/training/registry/`. Leave `metrics` equal to the measured values from `results.csv`, not a hand-written target. The detector confidence threshold stays separate from the recipe acceptance threshold.
3. Check the record before activation. The untrained DemoBox record is schema-valid and exits 2:

```bash
python3 tools/training/validate_registry.py --print-sha256 deploy/models/best_openvino_model
python3 tools/training/validate_registry.py \
  --registry tools/training/registry/demobox.json \
  --recipe src/smartline/inspection/contracts/recipes/demobox.json
```

An OpenVINO directory is hashed from the sorted relative paths and bytes of its `.xml` and `.bin` files. Class names must match the recipe product classes in order. A checksum, format, or class mismatch also exits 2.
4. Set the recipe model `artifactFileName` and version through `PUT /api/recipes/{id}`. The artifact path may be the OpenVINO directory, not only a single XML file.
5. Start Vision with `MODEL_PATH` pointing at that file or OpenVINO directory. Confirm `GET` on the vision health port reports the backend, and run one manual inspection. A `.pt` file uses Ultralytics. An ONNX file or an OpenVINO directory uses Ultralytics when it is installed, otherwise ONNX Runtime or OpenVINO plus the YOLO26 decoder. A missing runtime returns no boxes. The service does not invent detections.
6. Promote the recipe to active only after that local check. Production sorting on this Intel CPU should use OpenVINO. Do not claim a frame rate until the latency field from a real inspection has been measured.


## v0.2.0 integrity rules

Pillow is required for image verification. Rejected captures do not participate in validation, class counts or training. Recorded checksums are checked; duplicate image hashes stay in one split. Export into an empty directory; a rejected export returns exit code 2. Review the rejection list before training. Notebook exports call `tools/training/export.py`, which writes artifact-specific checksum and class-order sidecars. Keep these sidecars beside ONNX/PT files or inside the OpenVINO directory. The sidecar version must match the recipe version of the model; it is independent of the recipe record's integer revision.

No training run, GPU benchmark or exported model was produced during integration.
