!function(t) {
    "use strict";
    var n, e, _, o, s, r, i, a;
    n = Object.setPrototypeOf || {
        __proto__: []
    } instanceof Array && function(t, n) {
        t.__proto__ = n;
    } || function(t, n) {
        var e;
        for (e in n) if (n.hasOwnProperty(e)) t[e] = n[e];
    };
    e = Object.assign || function(t) {
        var n, e, _, o;
        for (n = 1, e = arguments.length; n < e; n++) {
            _ = arguments[n];
            for (o in _) if (Object.prototype.hasOwnProperty.call(_, o)) t[o] = _[o];
        }
        return t;
    };
    _;
    o;
    s;
    r = !1;
    i = [ 1, 2, 3 ];
    a = i;
    console.log(i[0]);
}();

