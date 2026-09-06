# Local embedding model

Margo's optional semantic memory uses
[sentence-transformers/all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2)
under its Apache-2.0 model license. This repository contains adapter code and pinned artifact
identities, not the model weights. Model download and optional Python dependencies are explicit
setup operations; do not redistribute weights without following their license.

The pinned repository revision and asset digests are in
`skills/chief-of-staff/scripts/memory_encoder.py`. Setup verifies the ONNX model using SHA256 and
the small tokenizer/config assets using their Git blob identities. It writes a completion manifest
only after every asset verifies. No downloaded Python or pickle code is executed.

The runtime uses ONNX Runtime's CPU provider and the locally loaded tokenizer, producing
384-dimensional vectors. Attention-mask mean pooling and L2 normalisation are followed by
token-count-weighted aggregation across all 256-token overflow chunks for long inputs.
Queries and memories use the same pipeline. The preprocessing, tokenizer identity, dimensions
and model revision determine the fingerprint stored beside every vector.

Inference is offline. It does not automatically download, call an embedding API or use cloud
fallback. Changing the pinned model/preprocessing requires reindexing incompatible records.
The index is rebuildable; memory claims and their authority remain in SQLite independently.

The model is small and primarily suited to English semantic similarity. Exact identifiers and
keywords remain important for names, acronyms and specialised terms. Similarity is not factual
confidence, permission, or proof of a trend. Evaluate retrieval on the intended workload before
changing the model.

Follow [semantic memory setup](how-to/semantic-memory.md) for the private optional environment.
