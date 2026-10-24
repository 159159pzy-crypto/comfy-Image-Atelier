"""Validate a tiny Triton CUDA kernel in the selected environment."""
import torch
import triton
import triton.language as tl


@triton.jit
def add_kernel(x, y, output, size: tl.constexpr, block: tl.constexpr):
    offsets = tl.program_id(0) * block + tl.arange(0, block)
    mask = offsets < size
    tl.store(output + offsets, tl.load(x + offsets, mask=mask) + tl.load(y + offsets, mask=mask), mask=mask)


def main():
    x = torch.arange(1024, device="cuda", dtype=torch.float32)
    y = torch.ones_like(x)
    output = torch.empty_like(x)
    add_kernel[(4,)](x, y, output, 1024, 256)
    torch.cuda.synchronize()
    torch.testing.assert_close(output, x + y)
    print(f"Triton GPU kernel verified: {triton.__version__}; {torch.cuda.get_device_name()}")


if __name__ == "__main__":
    main()
