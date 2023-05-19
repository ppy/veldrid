@import Metal;
@import QuartzCore;

#import "metal_mono_workaround.h"

@implementation CADisplayLinkProxy

- (nonnull instancetype)initWithCallback:(nonnull CADisplayLinkCallback)callback {
    _displayLink = [CADisplayLink displayLinkWithTarget:self selector: @selector(onDisplayLinkCallback:)];
    _callback = callback;

    [_displayLink addToRunLoop:[NSRunLoop currentRunLoop] forMode:NSRunLoopCommonModes];

    return self;
}

- (void)onDisplayLinkCallback:(id)displayLink {
    _callback((__bridge void*)self);
}

@end

void copyFromBuffer(id<MTLBlitCommandEncoder> encoder,
                    id<MTLBuffer> sourceBuffer,
                    NSUInteger sourceOffset,
                    NSUInteger sourceBytesPerRow,
                    NSUInteger sourceBytesPerImage,
                    MTLSize sourceSize,
                    id<MTLTexture> destinationTexture,
                    NSUInteger destinationSlice,
                    NSUInteger destinationLevel,
                    NSUInteger destinationOriginX,
                    NSUInteger destinationOriginY,
                    NSUInteger destinationOriginZ)
{
    [encoder copyFromBuffer:sourceBuffer
               sourceOffset:sourceOffset
          sourceBytesPerRow:sourceBytesPerRow
        sourceBytesPerImage:sourceBytesPerImage
                 sourceSize:sourceSize
                  toTexture:destinationTexture
           destinationSlice:destinationSlice
           destinationLevel:destinationLevel
          destinationOrigin:MTLOriginMake(destinationOriginX, destinationOriginY, destinationOriginZ)];
}

void copyFromTexture(id<MTLBlitCommandEncoder> encoder,
                     id<MTLTexture> sourceTexture,
                     NSUInteger sourceSlice,
                     NSUInteger sourceLevel,
                     MTLOrigin sourceOrigin,
                     MTLSize sourceSize,
                     id<MTLTexture> destinationTexture,
                     NSUInteger destinationSlice,
                     NSUInteger destinationLevel,
                     NSUInteger destinationOriginX,
                     NSUInteger destinationOriginY,
                     NSUInteger destinationOriginZ)
{
    [encoder copyFromTexture:sourceTexture
                 sourceSlice:sourceSlice
                 sourceLevel:sourceLevel
                sourceOrigin:sourceOrigin
                  sourceSize:sourceSize
                   toTexture:destinationTexture
            destinationSlice:destinationSlice
            destinationLevel:destinationLevel
           destinationOrigin:MTLOriginMake(destinationOriginX, destinationOriginY, destinationOriginZ)];
}

CADisplayLinkProxy* createDisplayLinkProxy(CADisplayLinkCallback callback)
{
    return [[CADisplayLinkProxy alloc] initWithCallback:callback];
}
