// smoke-server.mjs — serves an unpacked Release ZIP's wwwroot over local HTTP
// for the Station 7 browser smoke. The only transform: the WebView2 virtual
// host <base href="https://psx.local/"> is rewritten to "/" so a plain
// browser resolves the packaged relative URLs; every other byte is served
// exactly as packaged.
//
// Usage: node tools/smoke-server.mjs <wwwroot-dir> [port]

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const rootDir = path.resolve(process.argv[2] || 'bin/smoke-unpack/wwwroot');
const port = Number(process.argv[3] || 8123);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8'
};

const server = http.createServer((req, res) => {
  const urlPath = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
  const relative = urlPath === '/' ? 'index.html' : urlPath.replace(/^\/+/, '');
  const file = path.join(rootDir, relative);
  if (!file.startsWith(rootDir)) {
    res.writeHead(403).end();
    return;
  }
  fs.readFile(file, (error, data) => {
    if (error) {
      res.writeHead(404, { 'content-type': 'text/plain' }).end('404 ' + relative);
      return;
    }
    const ext = path.extname(file).toLowerCase();
    let body = data;
    if (relative === 'index.html') {
      body = Buffer.from(
        data.toString('utf8').replace('<base href="https://psx.local/">', '<base href="/">')
      );
    }
    res.writeHead(200, { 'content-type': MIME[ext] || 'application/octet-stream' }).end(body);
  });
});

server.listen(port, () => {
  console.log('[smoke] serving ' + rootDir + ' at http://localhost:' + port + '/');
});
