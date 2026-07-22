import os

from flask import Flask, jsonify

app = Flask(__name__)

@app.post('/analyze')
def analyze():
    return jsonify([])

@app.route('/health', methods=['GET'])
def health():
    return jsonify({'status': 'ok'})

if __name__ == "__main__":
    # 0.0.0.0 so PrivacyProxy can reach the mock from another container.
    port = int(os.environ.get("PORT", 5002))
    app.run(host="0.0.0.0", port=port)