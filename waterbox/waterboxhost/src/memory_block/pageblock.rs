use std::ptr::{null_mut, NonNull};
use core::ffi::c_void;
use crate::*;

/// wraps the allocation of a single PAGESIZE bytes of ram, and is safe-ish to call within a signal handler
#[derive(Debug)]
pub struct PageBlock {
	ptr: NonNull<u8>,
}

impl PageBlock {
	pub fn new() -> PageBlock {
		unsafe {
			let ptr = alloc();
			if ptr == null_mut() {
				panic!("PageBlock could not allocate memory!");
			} else {
				PageBlock {
					ptr: NonNull::new_unchecked(ptr as *mut u8),
				}
			}
		}
	}

	pub fn slice<'a>(&'a self) -> &'a [u8] {
		unsafe {
			std::slice::from_raw_parts(self.ptr.as_ptr(), PAGESIZE)
		}
	}
	pub fn slice_mut<'a>(&'a mut self) -> &'a mut [u8] {
		unsafe {
			std::slice::from_raw_parts_mut(self.ptr.as_ptr(), PAGESIZE)
		}
	}
	pub fn as_ptr(&self) -> *const u8 {
		self.ptr.as_ptr()
	}
	pub fn as_mut_ptr(&mut self) -> *mut u8 {
		self.ptr.as_ptr()
	}
}

impl Drop for PageBlock {
	fn drop(&mut self) {
		unsafe {
			let res = free(self.ptr.as_ptr() as *mut c_void);
			if !res {
				panic!("PageBlock could not free memory!");
			}
		}
	}
}

#[cfg(windows)]
use winapi::um::memoryapi::*;
#[cfg(windows)]
use winapi::um::winnt::*;
#[cfg(windows)]
unsafe fn alloc() -> *mut c_void {
	VirtualAlloc(null_mut(), PAGESIZE, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE) as *mut c_void
}
#[cfg(windows)]
unsafe fn free(ptr: *mut c_void) -> bool {
	match VirtualFree(ptr as *mut winapi::ctypes::c_void, 0, MEM_RELEASE) {
		0 => false,
		_ => true
	}
}

#[cfg(unix)]
use libc::*;
#[cfg(unix)]
unsafe fn alloc() -> *mut c_void {
	let ptr = mmap(null_mut(), PAGESIZE, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
	match ptr {
		MAP_FAILED => null_mut(),
		_ => ptr
	}
}
#[cfg(unix)]
unsafe fn free(ptr: *mut c_void) -> bool {
	let res = munmap(ptr, PAGESIZE);
	match res {
		0 => true,
		_ => false
	}
}

#[cfg(test)]
#[test]
fn basic_test() {
	let mut s = PageBlock::new();

	for x in s.slice().iter() {
		assert!(*x == 0);
	}
	let ml = s.slice_mut();
	for i in 0..PAGESIZE {
		ml[i] = i as u8;
	}
	let sl = s.slice();
	for i in 0..PAGESIZE {
		assert!(sl[i] == i as u8);
	}
}
