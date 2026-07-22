import os

from flask import Flask, jsonify, request

app = Flask(__name__)

# Each item in list is a mocked PII entry
_entities: list[dict] = []

@app.route("/health")
def health():
    return jsonify({"status": "ok"})

@app.post("/mock")
def mock():
    """
    The experiment.py forwards only the privacy_mask field of each row in ai4privacy/pii-masking-200k dataset.
    the request body looks like this:
    [ { "value": "06-184755-866851-3", "start": 57, "end": 75, "label": "PHONEIMEI" },
    { "value": "Optimization", "start": 138, "end": 150, "label": "JOBAREA" } ]
    :return:
    """

    privacy_mask = request.get_json()

    global _entities
    _entities = [
        {
            "entity_type": item["label"],
            "start": item["start"],
            "end": item["end"],
            "score": 1.0
        }
        for item in privacy_mask
    ]

    return jsonify({"status": "ok"})

@app.post("/analyze")
def analyze():
    return jsonify(_entities)

if __name__ == "__main__":
    # 0.0.0.0 so PrivacyProxy can reach the mock from another container.
    port = int(os.environ.get("PORT", 5002))
    app.run(host="0.0.0.0", port=port)