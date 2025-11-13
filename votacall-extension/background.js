// Votalink Responder Extension - Background Service Worker
// Maintains WebSocket connection regardless of page visibility
// UNIQUE_MARKER_VOTALINK_RESPONDER_EXTENSION_2025_11_13 - Used for path detection

(function() {
    'use strict';

    const WS_PORT = 9231;
    const WS_URL = `ws://127.0.0.1:${WS_PORT}/`;
    const DEFAULT_RETRY_INTERVAL = 60000; // 1 minute default
    const MAX_RETRY_DELAY = 5000; // 5 seconds max delay between retries

    let ws = null;
    let reconnectTimeout = null;
    let keepAliveInterval = null;
    let retryDelay = 1000;
    let connectionStatus = {
        connected: false,
        lastConnectAttempt: null,
        lastError: null,
        retryInterval: DEFAULT_RETRY_INTERVAL
    };

    // Load saved retry interval from storage (in milliseconds)
    chrome.storage.local.get(['retryInterval'], (result) => {
        if (result.retryInterval) {
            connectionStatus.retryInterval = result.retryInterval;
        } else {
            // Default to 60 seconds if not set
            connectionStatus.retryInterval = 60000;
            chrome.storage.local.set({ retryInterval: 60000 });
        }
    });

    // Listen for retry interval changes from popup
    chrome.storage.onChanged.addListener((changes, areaName) => {
        if (areaName === 'local' && changes.retryInterval) {
            connectionStatus.retryInterval = changes.retryInterval.newValue;
        }
    });

    // Update connection status in storage
    function updateStatus() {
        chrome.storage.local.set({ connectionStatus });
    }

    // Show notification when connected
    function showConnectedNotification() {
        try {
            chrome.notifications.create({
                type: 'basic',
                iconUrl: chrome.runtime.getURL('icon48.png'),
                title: 'Votalink Responder',
                message: 'Extension connected to VotalinkResponder',
                priority: 1
            });
        } catch (error) {
            console.log('[Background] Could not show notification (icons may be missing)');
        }
    }

    // Show notification when disconnected (only if was previously connected)
    function showDisconnectedNotification() {
        try {
            chrome.notifications.create({
                type: 'basic',
                iconUrl: chrome.runtime.getURL('icon48.png'),
                title: 'Votalink Responder',
                message: 'Extension disconnected from VotalinkResponder',
                priority: 1
            });
        } catch (error) {
            console.log('[Background] Could not show notification (icons may be missing)');
        }
    }

    // Show notification when app is not running (only once per session)
    let appNotRunningNotified = false;
    function showAppNotRunningNotification() {
        if (appNotRunningNotified) return; // Only show once
        
        try {
            chrome.notifications.create({
                type: 'basic',
                iconUrl: chrome.runtime.getURL('icon48.png'),
                title: 'Votalink Responder',
                message: 'VotalinkResponder app is not running. Please start it to enable connection.',
                priority: 2
            });
            appNotRunningNotified = true;
            
            // Reset notification flag after 5 minutes
            setTimeout(() => {
                appNotRunningNotified = false;
            }, 300000);
        } catch (error) {
            // Silently fail - notifications may not be available
        }
    }

    // Show toast notification in browser window (only on Votacall tabs)
    let toastShown = false;
    function showToastInBrowser(message) {
        if (toastShown) return; // Only show once per session
        
        // Inject toast only into Votacall webapp tabs
        chrome.tabs.query({ url: 'https://myvotacall.com/webphone/*' }, (tabs) => {
            if (!tabs || tabs.length === 0) return;
            
            // Filter to only valid Votacall tabs
            const validTabs = tabs.filter(tab => {
                const url = tab.url || '';
                return url.includes('myvotacall.com/webphone') && 
                       (url.includes('/agent-center') || url.includes('/webphone'));
            });
            
            validTabs.forEach(tab => {
                chrome.scripting.executeScript({
                    target: { tabId: tab.id },
                    func: (msg) => {
                        // Check if toast already exists
                        if (document.getElementById('votalink-toast')) return;
                        
                        const toast = document.createElement('div');
                        toast.id = 'votalink-toast';
                        toast.style.cssText = `
                            position: fixed;
                            top: 20px;
                            right: 20px;
                            background: linear-gradient(135deg, #1e3c72 0%, #2a5298 100%);
                            color: white;
                            padding: 16px 24px;
                            border-radius: 8px;
                            box-shadow: 0 4px 12px rgba(0,0,0,0.3);
                            z-index: 999999;
                            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
                            font-size: 14px;
                            max-width: 400px;
                            animation: slideIn 0.3s ease-out;
                        `;
                        toast.innerHTML = `
                            <div style="display: flex; align-items: center; gap: 12px;">
                                <div style="flex: 1;">
                                    <div style="font-weight: 600; margin-bottom: 4px;">Votalink Responder</div>
                                    <div style="font-size: 12px; opacity: 0.9;">${msg}</div>
                                </div>
                                <button id="votalink-toast-close" style="
                                    background: rgba(255,255,255,0.2);
                                    border: none;
                                    color: white;
                                    width: 24px;
                                    height: 24px;
                                    border-radius: 4px;
                                    cursor: pointer;
                                    font-size: 18px;
                                    line-height: 1;
                                    padding: 0;
                                ">×</button>
                            </div>
                            <style>
                                @keyframes slideIn {
                                    from { transform: translateX(100%); opacity: 0; }
                                    to { transform: translateX(0); opacity: 1; }
                                }
                            </style>
                        `;
                        document.body.appendChild(toast);
                        
                        // Close button handler
                        const closeBtn = toast.querySelector('#votalink-toast-close');
                        closeBtn.addEventListener('click', () => {
                            toast.style.animation = 'slideOut 0.3s ease-out';
                            setTimeout(() => toast.remove(), 300);
                        });
                        
                        // Auto-remove after 10 seconds
                        setTimeout(() => {
                            if (toast.parentNode) {
                                toast.style.animation = 'slideOut 0.3s ease-out';
                                setTimeout(() => toast.remove(), 300);
                            }
                        }, 10000);
                        
                        // Add slideOut animation
                        if (!document.getElementById('votalink-toast-styles')) {
                            const style = document.createElement('style');
                            style.id = 'votalink-toast-styles';
                            style.textContent = `
                                @keyframes slideOut {
                                    from { transform: translateX(0); opacity: 1; }
                                    to { transform: translateX(100%); opacity: 0; }
                                }
                            `;
                            document.head.appendChild(style);
                        }
                    },
                    args: [message]
                }).catch(() => {
                    // Tab might not allow script injection
                });
            });
        });
        
        toastShown = true;
        // Reset flag after 30 seconds so it can show again if needed
        setTimeout(() => {
            toastShown = false;
        }, 30000);
    }

    // Check if Votacall tab is open and on correct page
    async function hasVotacallTab() {
        return new Promise((resolve) => {
            chrome.tabs.query({ url: 'https://myvotacall.com/webphone/*' }, (tabs) => {
                if (!tabs || tabs.length === 0) {
                    resolve(false);
                    return;
                }
                // Verify tabs are actually on the webphone page (not just matching URL pattern)
                const validTabs = tabs.filter(tab => {
                    const url = tab.url || '';
                    return url.includes('myvotacall.com/webphone') && 
                           (url.includes('/agent-center') || url.includes('/webphone'));
                });
                resolve(validTabs.length > 0);
            });
        });
    }

    // Connect to WebSocket server (only if Votacall tab is open)
    async function connect() {
        try {
            if (ws && ws.readyState === WebSocket.OPEN) {
                // Check if we still have a Votacall tab - disconnect if not
                const hasTab = await hasVotacallTab();
                if (!hasTab) {
                    console.log('[Background] No Votacall tabs open - disconnecting');
                    ws.close();
                    return;
                }
                return; // Already connected and tab exists
            }

            // Only connect if Votacall tab is open
            const hasTab = await hasVotacallTab();
            if (!hasTab) {
                connectionStatus.lastError = 'No Votacall webphone tab open';
                updateStatus();
                // Don't schedule reconnect - wait for tab to open
                return;
            }

            connectionStatus.lastConnectAttempt = new Date().toISOString();
            connectionStatus.lastError = null;
            updateStatus();

            // Only log connection attempts if we haven't tried recently (reduce noise)
            const lastAttempt = connectionStatus.lastConnectAttempt ? new Date(connectionStatus.lastConnectAttempt) : null;
            const secondsSinceLastAttempt = lastAttempt ? (Date.now() - lastAttempt.getTime()) / 1000 : Infinity;
            
            // Suppress console logs for connection attempts when app isn't running
            // Only log first attempt or if we were previously connected
            if ((secondsSinceLastAttempt > 5 && connectionStatus.connected) || !connectionStatus.lastConnectAttempt) {
                console.log(`[Background] Connecting to WebSocket server at ${WS_URL}...`);
            }
            
            // Create WebSocket with error suppression wrapper
            try {
                ws = new WebSocket(WS_URL);
            } catch (error) {
                // This catch won't fire for connection refused, but handle other errors
                connectionStatus.lastError = 'Failed to create WebSocket connection';
                updateStatus();
                scheduleReconnect();
                return;
            }

            ws.onopen = () => {
                console.log('[Background] ✓ WebSocket connected successfully');
                connectionStatus.connected = true;
                connectionStatus.lastError = null;
                retryDelay = 1000; // Reset retry delay
                appNotRunningNotified = false; // Reset notification flag on successful connection
                toastShown = false; // Reset toast flag on successful connection
                updateStatus();
                showConnectedNotification();
                
                // Remove any existing toast notifications from Votacall tabs
                chrome.tabs.query({ url: 'https://myvotacall.com/webphone/*' }, (tabs) => {
                    if (!tabs || tabs.length === 0) return;
                    
                    const validTabs = tabs.filter(tab => {
                        const url = tab.url || '';
                        return url.includes('myvotacall.com/webphone') && 
                               (url.includes('/agent-center') || url.includes('/webphone'));
                    });
                    
                    validTabs.forEach(tab => {
                        chrome.scripting.executeScript({
                            target: { tabId: tab.id },
                            func: () => {
                                const toast = document.getElementById('votalink-toast');
                                if (toast) {
                                    toast.style.animation = 'slideOut 0.3s ease-out';
                                    setTimeout(() => toast.remove(), 300);
                                }
                            }
                        }).catch(() => {
                            // Tab might not allow script injection
                        });
                    });
                });

                // Send identification message with Votacall tab status
                updateIdentification();

                // Start keepalive ping every 30 seconds to prevent connection timeout
                startKeepAlive();
            };

            ws.onmessage = (event) => {
                // Forward messages to content scripts via storage event
                try {
                    const data = JSON.parse(event.data);
                    if (data.type === 'call-answer') {
                        // Broadcast to all tabs
                        const DEBUG_MODE = false; // Set to true for verbose logging
                        
                        if (DEBUG_MODE) {
                            console.log('[Background] ========================================');
                            console.log('[Background] RECEIVED call-answer FROM WEBSOCKET');
                            console.log('[Background] Data:', JSON.stringify(data, null, 2));
                            console.log('[Background] Querying for Votacall tabs...');
                        }
                        
                        chrome.tabs.query({ url: 'https://myvotacall.com/webphone/*' }, (tabs) => {
                            if (DEBUG_MODE) {
                                console.log(`[Background] Found ${tabs.length} tab(s) matching Votacall URL`);
                                console.log('[Background] Tab IDs:', tabs.map(t => t.id));
                            }
                            
                            if (tabs.length === 0) {
                                console.warn('[Background] ⚠ No Votacall tabs found!');
                                return;
                            }
                            
                            tabs.forEach(tab => {
                                if (DEBUG_MODE) {
                                    console.log(`[Background] ========================================`);
                                    console.log(`[Background] Attempting to send message to tab ${tab.id}`);
                                    console.log(`[Background] Tab URL: ${tab.url}`);
                                    console.log(`[Background] Tab title: ${tab.title}`);
                                    console.log(`[Background] Message payload:`, {
                                        type: 'call-answer',
                                        data: data
                                    });
                                }
                                
                                try {
                                    chrome.tabs.sendMessage(tab.id, {
                                        type: 'call-answer',
                                        data: data
                                    }, (response) => {
                                        if (chrome.runtime.lastError) {
                                            console.error(`[Background] ✗ ERROR sending to tab ${tab.id}: ${chrome.runtime.lastError.message}`);
                                            if (DEBUG_MODE) {
                                                console.error(`[Background] Error code:`, chrome.runtime.lastError);
                                            }
                                        } else {
                                            if (DEBUG_MODE) {
                                                console.log(`[Background] ✓ Message sent to tab ${tab.id}`);
                                                console.log(`[Background] Response:`, response);
                                            }
                                        }
                                    });
                                } catch (error) {
                                    console.error(`[Background] ✗ EXCEPTION sending to tab ${tab.id}:`, error);
                                    if (DEBUG_MODE) {
                                        console.error(`[Background] Stack:`, error.stack);
                                    }
                                }
                            });
                        });
                    } else if (data.type === 'call-hangup') {
                        // Broadcast to all tabs
                        chrome.tabs.query({ url: 'https://myvotacall.com/webphone/*' }, (tabs) => {
                            console.log(`[Background] Broadcasting call-hangup to ${tabs.length} tab(s)`);
                            tabs.forEach(tab => {
                                chrome.tabs.sendMessage(tab.id, {
                                    type: 'call-hangup',
                                    data: data
                                }, (response) => {
                                    if (chrome.runtime.lastError) {
                                        console.log(`[Background] Error sending to tab ${tab.id}: ${chrome.runtime.lastError.message}`);
                                    } else {
                                        console.log(`[Background] Message sent to tab ${tab.id}`);
                                    }
                                });
                            });
                        });
                    } else if (data.type === 'pong') {
                        // Keepalive pong received - connection is alive
                        console.log('[Background] Keepalive pong received');
                    }
                } catch (error) {
                    console.error('[Background] Error handling message:', error);
                }
            };

            ws.onerror = (error) => {
                // Suppress all error logging - connection refused is expected when app isn't running
                // The browser console will still show the error, but we won't add to it
                connectionStatus.lastError = 'VotalinkResponder app is not running';
                updateStatus();
                
                // Show notification and toast once that app needs to be started
                if (!connectionStatus.connected) {
                    showAppNotRunningNotification();
                    showToastInBrowser('VotalinkResponder app is not running. Please start VotalinkResponder.exe');
                }
            };

            ws.onclose = (event) => {
                // Don't log or notify if connection was never established (app not running)
                const wasConnected = connectionStatus.connected;
                connectionStatus.connected = false;
                stopKeepAlive();
                
                // Only log if we were previously connected
                if (wasConnected) {
                    console.log('[Background] WebSocket connection closed');
                    showDisconnectedNotification();
                } else {
                    // Connection failed - app probably not running
                    connectionStatus.lastError = 'VotalinkResponder app is not running';
                    showAppNotRunningNotification();
                    showToastInBrowser('VotalinkResponder app is not running. Please start VotalinkResponder.exe');
                }
                
                updateStatus();
                scheduleReconnect();
            };

        } catch (error) {
            // Suppress all connection errors - they're expected when app isn't running
            connectionStatus.lastError = 'VotalinkResponder app is not running';
            updateStatus();
            showAppNotRunningNotification();
            showToastInBrowser('VotalinkResponder app is not running. Please start VotalinkResponder.exe');
            scheduleReconnect();
        }
    }

    // Schedule reconnection with exponential backoff
    function scheduleReconnect() {
        if (reconnectTimeout) {
            clearTimeout(reconnectTimeout);
        }

        reconnectTimeout = setTimeout(() => {
            console.log(`[Background] Attempting to reconnect (delay: ${retryDelay}ms)...`);
            connect();
            retryDelay = Math.min(retryDelay * 2, MAX_RETRY_DELAY);
        }, retryDelay);
    }

    // Keepalive ping to prevent connection timeout and service worker suspension
    function startKeepAlive() {
        stopKeepAlive(); // Clear any existing interval
        
        // Send ping every 30 seconds
        keepAliveInterval = setInterval(() => {
            if (ws && ws.readyState === WebSocket.OPEN) {
                try {
                    // Send a ping message to keep connection alive
                    ws.send(JSON.stringify({ type: 'ping', timestamp: Date.now() }));
                    console.log('[Background] Keepalive ping sent');
                } catch (error) {
                    console.error('[Background] Error sending keepalive ping:', error);
                }
            }
        }, 30000); // 30 seconds
    }

    function stopKeepAlive() {
        if (keepAliveInterval) {
            clearInterval(keepAliveInterval);
            keepAliveInterval = null;
        }
    }

    // Periodic retry check (user-defined interval) - only if Votacall tab exists
    function startPeriodicRetry() {
        setInterval(async () => {
            const hasTab = await hasVotacallTab();
            if (!connectionStatus.connected && hasTab && ws && ws.readyState !== WebSocket.CONNECTING) {
                console.log(`[Background] Periodic retry check - attempting connection...`);
                retryDelay = 1000; // Reset delay for periodic retry
                connect();
            } else if (connectionStatus.connected && !hasTab) {
                // Disconnect if no Votacall tabs open
                console.log('[Background] No Votacall tabs - disconnecting');
                if (ws) {
                    ws.close();
                }
            }
        }, connectionStatus.retryInterval);
    }

    // Monitor tab changes to connect/disconnect as needed
    chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
        if (!tab.url) return;
        
        const isVotacallPage = tab.url.includes('myvotacall.com/webphone') && 
                              (tab.url.includes('/agent-center') || tab.url.includes('/webphone'));
        
        if (changeInfo.status === 'complete' && isVotacallPage) {
            // Votacall tab loaded - try to connect
            if (!connectionStatus.connected) {
                await connect();
            } else {
                // Update identification to reflect new tab
                await updateIdentification();
            }
        } else if (changeInfo.status === 'complete' && !isVotacallPage && connectionStatus.connected) {
            // Tab navigated away from Votacall - check if any tabs remain
            const hasTab = await hasVotacallTab();
            if (!hasTab && ws && ws.readyState === WebSocket.OPEN) {
                console.log('[Background] No valid Votacall tabs - disconnecting');
                ws.close();
            } else if (hasTab) {
                // Update identification
                await updateIdentification();
            }
        }
    });

    // Update identification message
    async function updateIdentification() {
        if (!ws || ws.readyState !== WebSocket.OPEN) return;
        
        const hasTab = await hasVotacallTab();
        chrome.tabs.query({ url: 'https://myvotacall.com/webphone/*' }, (tabs) => {
            const validTabs = tabs ? tabs.filter(t => {
                const url = t.url || '';
                return url.includes('myvotacall.com/webphone') && 
                       (url.includes('/agent-center') || url.includes('/webphone'));
            }) : [];
            
            // Create a unique fingerprint for this extension to help locate its folder
            // We'll use a hash of background.js content as a unique identifier
            const extensionFingerprint = {
                name: 'Votalink Responder Extension', // Must match manifest.json name
                version: '1.0.8', // Must match manifest.json version
                fileHash: null // Hash of background.js for identification
            };
            
            // Try to get manifest data
            try {
                const manifest = chrome.runtime.getManifest();
                if (manifest) {
                    extensionFingerprint.name = manifest.name || extensionFingerprint.name;
                    extensionFingerprint.version = manifest.version || extensionFingerprint.version;
                }
            } catch (e) {
                // Fallback to defaults
            }
            
            // Send unique marker string that we'll search for in background.js files
            // This marker is embedded in the background.js file itself
            extensionFingerprint.fileHash = 'UNIQUE_MARKER_VOTALINK_RESPONDER_EXTENSION_2025_11_13';
            
            const browserInfo = {
                type: 'extension-identify',
                browser: navigator.userAgent.includes('Edg') ? 'Edge' : 
                         navigator.userAgent.includes('Chrome') ? 'Chrome' : 
                         navigator.userAgent.includes('Firefox') ? 'Firefox' : 'Unknown',
                version: navigator.userAgent.match(/(Chrome|Edg|Firefox)\/([\d.]+)/)?.[2] || '',
                userAgent: navigator.userAgent,
                source: 'background',
                hasVotacallTab: validTabs.length > 0,
                votacallTabCount: validTabs.length,
                extensionFingerprint: extensionFingerprint // Send fingerprint for path detection
            };
            try {
                ws.send(JSON.stringify(browserInfo));
            } catch (e) {}
        });
    }

    chrome.tabs.onRemoved.addListener(async (tabId) => {
        // Check if any valid Votacall tabs remain
        const hasTab = await hasVotacallTab();
        if (!hasTab && ws && ws.readyState === WebSocket.OPEN) {
            console.log('[Background] Last valid Votacall tab closed - disconnecting');
            ws.close();
        } else if (hasTab) {
            // Update identification if tabs remain
            await updateIdentification();
        }
    });

    // Use chrome.alarms to keep service worker alive
    chrome.alarms.create('keepAlive', { periodInMinutes: 1 });

    chrome.alarms.onAlarm.addListener((alarm) => {
        if (alarm.name === 'keepAlive') {
            // This keeps the service worker alive
            console.log('[Background] Keepalive alarm fired');
            // Check connection status
            if (ws && ws.readyState === WebSocket.OPEN) {
                connectionStatus.connected = true;
            } else if (ws && ws.readyState === WebSocket.CLOSED) {
                connectionStatus.connected = false;
                if (!reconnectTimeout) {
                    scheduleReconnect();
                }
            }
            updateStatus();
        }
    });

    // Initial connection attempt (only if Votacall tab exists)
    hasVotacallTab().then(hasTab => {
        if (hasTab) {
            connect();
        } else {
            connectionStatus.lastError = 'No Votacall webphone tab open';
            updateStatus();
        }
    });
    startPeriodicRetry();

    // Listen for messages from popup and content scripts
    chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
        // Log ALL messages for debugging
        const msgType = request.type || request.action || 'unknown';
        const senderInfo = sender?.tab ? `tab ${sender.tab.id}` : (sender?.id ? 'popup' : 'unknown');
        console.log(`[Background] 📨 Received message: type="${msgType}", sender=${senderInfo}`, request);
        
        if (request.action === 'getStatus') {
            sendResponse(connectionStatus);
            return false; // Synchronous response
        } else if (request.action === 'reconnect') {
            retryDelay = 1000;
            connect();
            sendResponse({ success: true });
            return false; // Synchronous response
        } else if (request.action === 'setRetryInterval') {
            connectionStatus.retryInterval = request.interval;
            chrome.storage.local.set({ retryInterval: request.interval });
            sendResponse({ success: true });
            return false; // Synchronous response
        } else if (request.type === 'call-answer-reply') {
            console.log(`[Background] ✅✅✅ CALL-ANSWER-REPLY RECEIVED ✅✅✅`);
            console.log(`[Background] Success: ${request.success}`);
            console.log(`[Background] Message: "${request.message}"`);
            console.log(`[Background] Full request:`, request);
            console.log(`[Background] WebSocket exists: ${!!ws}`);
            console.log(`[Background] WebSocket state: ${ws ? `readyState=${ws.readyState} (OPEN=${WebSocket.OPEN})` : 'null'}`);
            
            // Forward reply from content script to server
            if (ws && ws.readyState === WebSocket.OPEN) {
                try {
                    const replyJson = JSON.stringify(request);
                    console.log(`[Background] 📤 Sending reply JSON to server:`, replyJson);
                    ws.send(replyJson);
                    console.log(`[Background] ✅✅✅ REPLY SENT TO SERVER ✅✅✅`);
                } catch (error) {
                    console.error('[Background] ❌ ERROR sending reply to server:', error);
                }
            } else {
                const state = ws ? `readyState=${ws.readyState} (OPEN=${WebSocket.OPEN})` : 'null';
                console.error(`[Background] ❌❌❌ CANNOT FORWARD REPLY - WebSocket not connected (${state}) ❌❌❌`);
            }
            sendResponse({ success: true });
            return false; // Synchronous response
        } else if (request.type === 'call-hangup-reply') {
            console.log(`[Background] ✅✅✅ CALL-HANGUP-REPLY RECEIVED ✅✅✅`);
            console.log(`[Background] Success: ${request.success}`);
            console.log(`[Background] Message: "${request.message}"`);
            
            // Forward reply from content script to server
            if (ws && ws.readyState === WebSocket.OPEN) {
                try {
                    const replyJson = JSON.stringify(request);
                    console.log(`[Background] 📤 Sending hangup reply JSON to server:`, replyJson);
                    ws.send(replyJson);
                    console.log(`[Background] ✅✅✅ HANGUP REPLY SENT TO SERVER ✅✅✅`);
                } catch (error) {
                    console.error('[Background] ❌ ERROR sending hangup reply to server:', error);
                }
            } else {
                const state = ws ? `readyState=${ws.readyState} (OPEN=${WebSocket.OPEN})` : 'null';
                console.error(`[Background] ❌❌❌ CANNOT FORWARD HANGUP REPLY - WebSocket not connected (${state}) ❌❌❌`);
            }
            sendResponse({ success: true });
            return false; // Synchronous response
        }
        
        // Return false for unhandled messages
        return false;
    });

    console.log('[Background] Votalink Responder Extension background service started');
})();

