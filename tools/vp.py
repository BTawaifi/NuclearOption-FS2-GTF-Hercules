import struct, sys, glob, os, re
def entries(path):
    with open(path,'rb') as f:
        magic, ver, off, n = struct.unpack('<4siii', f.read(16))
        if magic != b'VPVP': return
        f.seek(off); d=[]
        for _ in range(n):
            o,s,name,ts = struct.unpack('<ii32si', f.read(44))
            name=name.split(b'\0')[0].decode('latin1')
            if s==0 and o==0 and name!='..': d.append(name); continue
            if name=='..': d.pop(); continue
            if s==0: d.append(name); continue
            yield '/'.join(d+[name]), o, s
def extract(vp, pattern, out):
    with open(vp,'rb') as f:
        for p,o,s in entries(vp):
            if re.search(pattern,p,re.I):
                f.seek(o); dst=os.path.join(out,os.path.basename(p)); open(dst,'wb').write(f.read(s)); print('X',vp,p,s)
if __name__=='__main__':
    if sys.argv[1]=='list':
        for vp in sys.argv[3:]:
            for p,o,s in entries(vp):
                if re.search(sys.argv[2],p,re.I): print(vp,p,s)
    else: extract(sys.argv[2],sys.argv[3],sys.argv[4])
