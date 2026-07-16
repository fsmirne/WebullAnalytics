// ==UserScript==
// @name         Webull Header Dump for wa fetch
// @namespace    webullanalytics
// @version      1.3
// @description  Capture Webull consumer-web session headers from your real logged-in session and hand them to `wa sniff`.
// @match        https://*.webull.com/*
// @run-at       document-start
// @grant        unsafeWindow
// @grant        GM_registerMenuCommand
// @grant        GM_xmlhttpRequest
// @connect      127.0.0.1
// @connect      localhost
// ==/UserScript==

// This runs inside your normal, already-logged-in Webull tab (default browser profile) — so there is no second
// Webull web session and nothing to trip "multiple sign-in". It hooks fetch/XMLHttpRequest, records the durable
// session headers off any authenticated request, and (on the Tampermonkey menu command) POSTs them to the local
// listener started by `wa sniff`, which writes them into webull.headers in api-config.json.
//
// Tampermonkey runs this in an ISOLATED world, so patching the sandbox's own window.fetch / XMLHttpRequest would
// NOT affect the page objects Webull actually uses — the hooks would never fire. We patch unsafeWindow instead
// (the page's REAL window), which escapes the sandbox without injecting a <script> (so page CSP can't block it).
//
// Usage: run `wa sniff`, then in this tab pick Tampermonkey -> "Dump Webull headers to wa". If it says no
// access_token yet, RELOAD the tab (the hook must be in place before Webull makes its requests), click around
// (e.g. open Orders) so the page makes an authenticated request, then run the command again.

(function () {
	'use strict';

	// Must match ListenPort in Sniff/SniffCommand.cs.
	const WA_PORT = 9223;

	// Full header set the sniffer historically captured. wa fetch drops x-s/x-sv and regenerates t_time, and nothing
	// consumes t_token, but we send the whole set for parity and future-proofing — wa persists whatever it receives.
	const KEYS = ['access_token', 'did', 'lzone', 'osv', 'ph', 't_token', 'tz', 'ver', 'x-s', 'x-sv'];
	const keySet = new Set(KEYS);
	const captured = {};

	function record(name, value) {
		if (name == null || value == null) return;
		const k = String(name).toLowerCase();
		if (keySet.has(k)) {
			const v = String(value);
			if (v.length > 0) captured[k] = v;
		}
	}

	// Patch the PAGE's real objects via unsafeWindow so the hooks see Webull's own fetch/XHR calls.
	const w = (typeof unsafeWindow !== 'undefined') ? unsafeWindow : window;

	const origSetHeader = w.XMLHttpRequest.prototype.setRequestHeader;
	w.XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
		try { record(name, value); } catch (e) { /* ignore */ }
		return origSetHeader.apply(this, arguments);
	};

	const origFetch = w.fetch;
	if (origFetch) {
		w.fetch = function (input, init) {
			try {
				const h = (init && init.headers) || (input && input.headers);
				if (h) {
					if (typeof h.forEach === 'function' && !Array.isArray(h)) h.forEach((v, k) => record(k, v));
					else if (Array.isArray(h)) h.forEach((pair) => record(pair[0], pair[1]));
					else Object.keys(h).forEach((k) => record(k, h[k]));
				}
			} catch (e) { /* ignore */ }
			return origFetch.apply(this, arguments);
		};
	}

	function send() {
		const count = Object.keys(captured).length;
		if (!captured['access_token']) {
			alert('Webull headers not captured yet.\nReload the tab, then click around (e.g. open Orders) so Webull makes a request, then run this again.');
			return;
		}
		GM_xmlhttpRequest({
			method: 'POST',
			url: 'http://127.0.0.1:' + WA_PORT + '/headers',
			headers: { 'Content-Type': 'application/json' },
			data: JSON.stringify(captured),
			onload: function (res) { alert('Sent ' + count + ' header(s) to wa (HTTP ' + res.status + ').\n' + (res.responseText || '')); },
			onerror: function () { alert('Could not reach wa on port ' + WA_PORT + '.\nIs `wa sniff` running?'); }
		});
	}

	GM_registerMenuCommand('Dump Webull headers to wa', send);
})();
