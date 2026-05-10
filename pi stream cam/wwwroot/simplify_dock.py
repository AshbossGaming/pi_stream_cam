import re

file_path = r'C:\Users\ash12\RiderProjects\pi stream cam\pi stream cam\wwwroot\dock.html'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Remove the isApp/login redirect logic at the start of script
# The server now handles all auth via /login endpoint
old_start = '''        const urlParams = new URLSearchParams(window.location.search);
        const isApp = urlParams.get('app') === 'true' || 
                      navigator.userAgent.includes('PiStreamCamApp');
        const APP_KEY = 'pi-stream-cam-mobile-v1';
        
        // If browser access, check auth and redirect to /login if needed
        if (!isApp) {
            fetch('/dock', { method: 'GET', credentials: 'include' })
                .then(r => { if (!r.ok) window.location.href = '/login'; });
        }'''

new_start = '''        // Auth is handled by server via /login endpoint
        // App sends X-App-Key header, browser uses password login'''

content = content.replace(old_start, new_start)

# Remove the APP_KEY and isApp logic from apiCall
content = re.sub(r'        const APP_KEY = .*?\n', '', content)
content = re.sub(r'        // Send app key if accessed from app.*?\n', '', content, flags=re.DOTALL)

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)

print('Simplified dock.html - server handles all auth')
