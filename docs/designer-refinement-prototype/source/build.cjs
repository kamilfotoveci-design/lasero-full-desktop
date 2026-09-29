const fs=require('fs'),path=require('path');
const repo=path.resolve(process.argv[2]||path.join(__dirname,'../../..'));
const output=path.resolve(process.argv[3]||path.join(__dirname,'../index.html'));
const asset=name=>'data:image/png;base64,'+fs.readFileSync(path.join(repo,'Lasero.App/Assets',name)).toString('base64');
let html=fs.readFileSync(path.join(__dirname,'prototype.html'),'utf8').replace('__ICONS__',fs.readFileSync(path.join(__dirname,'icons.json'),'utf8')).replaceAll('__LOGO__',asset('LaseroWordmark.png')).replaceAll('__AVATAR__',asset('KamilAvatar.png'));
fs.writeFileSync(output,html);console.log(output);
