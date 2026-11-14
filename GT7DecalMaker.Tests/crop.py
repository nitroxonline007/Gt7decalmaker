import sys
from PIL import Image
n=sys.argv[1]
s=Image.open(n+'.png').convert('RGB'); r=Image.open(n+'_render.png').convert('RGB')
box=(400,270,900,480); sc=r.width/s.width
a=s.crop(box).resize((750,315)); b=r.crop(tuple(int(v*sc) for v in box)).resize((750,315),Image.NEAREST)
o=Image.new('RGB',(750,640),'white'); o.paste(a,(0,0)); o.paste(b,(0,325)); o.save(n+'_crop.png')
