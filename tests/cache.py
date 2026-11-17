"""Exercise the real WMS gateway against a local tile server; no external map downloads."""
import argparse
import http.server
import json
import pathlib
import socket
import statistics
import struct
import subprocess
import tempfile
import threading
import time
import urllib.parse
import urllib.request
import zlib

ROOT = pathlib.Path(__file__).resolve().parents[1]


def png():
    def chunk(kind, data):
        return struct.pack('!I', len(data)) + kind + data + struct.pack('!I', zlib.crc32(kind + data))
    pixels = b''.join(b'\0' + bytes((x * 17 + y * 31 + c * 73) % 256 for x in range(256) for c in range(3)) for y in range(256))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!IIBBBBB', 256, 256, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b'')


class Tiles(http.server.BaseHTTPRequestHandler):
    body = png()
    requests = 0
    policy = 'public, max-age=3600'
    delay = 0

    def do_GET(self):
        type(self).requests += 1
        if self.delay:
            time.sleep(self.delay)
        self.send_response(200)
        self.send_header('Content-Type', 'image/png')
        self.send_header('Content-Length', str(len(self.body)))
        self.send_header('Cache-Control', self.policy)
        self.end_headers()
        self.wfile.write(self.body)

    def log_message(self, *_):
        pass


def unused_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--baseline', action='store_true', help='Report timings without enforcing the speedup.')
    args = parser.parse_args()
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Tiles)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    process = None
    with tempfile.TemporaryDirectory(prefix='sampling-cache-test-') as directory:
        folder = pathlib.Path(directory)
        settings = folder / 'settings.json'
        port = unused_port()
        template = f'http://127.0.0.1:{server.server_port}/tile/{{z}}/{{x}}/{{y}}.png'
        settings.write_text(json.dumps({'Port': port, 'CustomUrl': template, 'CustomName': 'Local cache fixture'}), encoding='utf-8')

        def start():
            child = subprocess.Popen([str(ROOT / 'build' / 'SamplingBasemapGateway.exe'), '--settings', str(settings), '--cache-root', str(folder / 'cache')])
            for _ in range(100):
                try:
                    urllib.request.urlopen(f'http://127.0.0.1:{port}/health', timeout=0.5).close()
                    return child
                except OSError:
                    if child.poll() is not None:
                        raise RuntimeError('Gateway exited during startup')
                    time.sleep(0.05)
            child.terminate()
            child.wait()
            raise RuntimeError('Gateway startup timed out')

        def render(shift=0, prefix='tile', size=1000):
            if prefix != 'tile':
                value = json.loads(settings.read_text())
                value['CustomUrl'] = template.replace('/tile/', '/' + prefix + '/')
                settings.write_text(json.dumps(value), encoding='utf-8')
            h = 20037508.342789244
            bbox = [-h / 4 + 1234 + shift, -h / 4 + 1234, h / 4 - 1234 + shift, h / 4 - 1234]
            query = urllib.parse.urlencode({'REQUEST': 'GetMap', 'LAYERS': 'basemap', 'FORMAT': 'image/png', 'SRS': 'EPSG:3857', 'BBOX': ','.join(map(str, bbox)), 'WIDTH': size, 'HEIGHT': size})
            started = time.perf_counter()
            with urllib.request.urlopen(f'http://127.0.0.1:{port}/wms/custom/0?{query}', timeout=30) as response:
                image = response.read()
            assert image.startswith(b'\x89PNG'), image[:200]
            return (time.perf_counter() - started) * 1000, image

        try:
            process = start()
            cold, image = render()
            count = Tiles.requests
            repeated = [render()[0] for _ in range(7)]
            assert Tiles.requests == count, 'Repeated view downloaded tiles again'
            shifted = [render(shift)[0] for shift in (10, 20, 30, 40, 50, 60, 70)]
            assert Tiles.requests == count, 'Small pans re-downloaded overlapping tiles'
            repeat_ms, pan_ms = statistics.median(repeated), statistics.median(shifted)
            print(json.dumps({'cold_ms': round(cold, 1), 'repeat_median_ms': round(repeat_ms, 1), 'cached_pan_median_ms': round(pan_ms, 1), 'repeat_speedup': round(pan_ms / repeat_ms, 2), 'upstream_requests': count}))
            if not args.baseline:
                assert repeat_ms < pan_ms * 0.6, 'Repeated views still recompose the image instead of reusing it'
            print('PASS repeated and overlapping views reuse tiles')
            process.terminate()
            process.wait()
            process = start()
            render()
            assert Tiles.requests == count, 'Disk cache did not survive gateway restart'
            print('PASS disk cache survives gateway restart')
            tiles = list((folder / 'cache').rglob('*.tile'))
            assert len(tiles) == count, 'Test did not use its isolated disk cache'
            tiles[0].write_bytes(b'broken cached image')
            process.terminate()
            process.wait()
            process = start()
            render()
            assert Tiles.requests == count + 1, 'Corrupt cached image was not repaired'
            print('PASS corrupt cached image is re-downloaded')
            count = Tiles.requests
            for tile in (folder / 'cache').rglob('*.tile'):
                tile.unlink()
            render(83)
            assert Tiles.requests == count, 'Pan did not use in-memory overlapping tiles'
            print('PASS overlapping pans use the memory tile cache')
            render(size=500)
            assert Tiles.requests > count, 'New zoom level did not fetch its own tiles'
            count = Tiles.requests
            render(size=500)
            assert Tiles.requests == count, 'Visited zoom level was not cached'
            print('PASS each visited zoom level is cached separately')
            Tiles.policy = 'No-Store'
            render(prefix='no-store')
            count = Tiles.requests
            render(prefix='no-store')
            assert Tiles.requests > count, 'no-store source was incorrectly cached'
            print('PASS no-store applies to both tiles and rendered images')
            Tiles.policy = 'No-Cache'
            render(prefix='no-cache')
            count = Tiles.requests
            render(prefix='no-cache')
            assert Tiles.requests > count, 'no-cache source was incorrectly reused'
            print('PASS no-cache applies to both tiles and rendered images')
            Tiles.policy = 'public, max-age=1'
            render(prefix='expires')
            count = Tiles.requests
            time.sleep(1.2)
            render(prefix='expires')
            assert Tiles.requests > count, 'Expired tiles or views were reused'
            print('PASS source expiry invalidates cached views')
            print('CACHE INTEGRATION PASSED')
        finally:
            if process is not None:
                process.terminate()
                process.wait()
            server.shutdown()


if __name__ == '__main__':
    main()
