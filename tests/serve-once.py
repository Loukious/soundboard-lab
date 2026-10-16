"""A local, single-request HTTP fixture for the desktop import workflow."""
from http.server import HTTPServer, BaseHTTPRequestHandler
from pathlib import Path
import sys

payload = Path(sys.argv[2]).read_bytes()
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'audio/wav')
        self.send_header('Content-Disposition', 'attachment; filename="downloaded-tone.wav"')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

server = HTTPServer(('127.0.0.1', int(sys.argv[1])), Handler)
print('ready', flush=True)
server.handle_request()
server.server_close()
