file_path = r'C:\Users\ash12\RiderProjects\pi stream cam\pi stream cam\Program.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Fix redirect after POST login - should go to /dock not /
content = content.replace('context.Response.Redirect("/");', 'context.Response.Redirect("/dock.html");')

# Fix app key redirect to /dock.html
content = content.replace('context.Response.Redirect("/dock");', 'context.Response.Redirect("/dock.html");')

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)

print('Fixed redirects to /dock.html')
