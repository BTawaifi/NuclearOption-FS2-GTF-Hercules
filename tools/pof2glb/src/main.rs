use std::{env, fs::File, path::PathBuf};
fn main() {
    let a: Vec<String> = env::args().collect();
    let path = PathBuf::from(&a[1]);
    let model = pof::Parser::new(File::open(&path).unwrap()).unwrap().parse(path.clone()).unwrap();
    let mut bytes = Vec::new();
    model.write_gltf(&mut bytes, true).unwrap();
    std::fs::write(&a[2], bytes).unwrap();
    println!("wrote {}", a[2]);
}
