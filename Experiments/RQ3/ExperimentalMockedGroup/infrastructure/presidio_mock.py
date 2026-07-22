import os

from flask import Flask, jsonify, request

app = Flask(__name__)

# The PII values of the CURRENT dataset row ("the solution"). experiment.py sets
# them via POST /mock before each data point; /analyze then finds them in whatever
# text the proxy sends
_pii_values: list[dict] = []

@app.post('/mock')
def mock():
    global _pii_values
    _pii_values = []
    _pii_values = request.get_json()
    return jsonify({'status': 'ok'})

@app.post('/analyze')
def analyze():
    text = request.get_json().get('text', '')
    entities = []
    for pii in _pii_values:
        value = pii['value']
        start = text.find(value)
        while start != -1: # every occurrence of the value
            entities.append({
                'entity_type': pii['type'],
                'start': start,
                'end': start + len(value),
                'score': 1.0,
            })
            start = text.find(value, start + 1)
    return jsonify(entities)

@app.route('/health', methods=['GET'])
def health():
    return jsonify({'status': 'ok'})

if __name__ == "__main__":
    # 0.0.0.0 so PrivacyProxy can reach the mock from another container.
    port = int(os.environ.get("PORT", 5002))
    app.run(host="0.0.0.0", port=port)